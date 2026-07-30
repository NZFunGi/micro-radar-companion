using System.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Simplify;

namespace MicroRadarCompanion;

// Ports the offline Python/shapely pipeline this project used to build the
// firmware's original EmbeddedCoastline.h data into C#, so range/location
// changes can regenerate a real coastline instead of falling back to the
// device's own rough on-the-fly classifier. Same method, same confirmed-
// correct classification sign, same per-device-grid-cell majority decision -
// just running on the desktop where it can afford real GIS work.
public static class CoastlineGenerator
{
    // 111.32 km/degree of latitude - used for BOTH axes here (matching
    // Projection::ToScreen's own degree-symmetric treatment of the radius),
    // scaled by cos(latitude) for longitude so 1 unit in this local frame is
    // a real meter in both directions at the configured center.
    private const double MetersPerDegree = 111320.0;
    private const int ScreenSize = 240;

    public static async Task<List<(double Lat, double Lon)>> GenerateAsync(
        double centerLat, double centerLon, double radiusDeg, int gridSize,
        IProgress<string> progress, CancellationToken ct)
    {
        // Cap the actual Overpass query area independent of the requested
        // radar radius - a dense coastline over a very large area is
        // expensive enough for Overpass's own infrastructure to time out
        // regardless of how patient this client is (mirrors the same
        // reasoning behind the firmware's own live-fetch radius cap, just
        // larger here since a desktop client can afford a bigger one-shot
        // fetch). 2.0 degrees is proven to work reliably against the real
        // Overpass API for this project's coastline density.
        const double fetchRadiusCap = 2.0;
        var fetchRadius = Math.Min(radiusDeg * 1.05, fetchRadiusCap);
        if (radiusDeg > fetchRadiusCap)
        {
            progress.Report($"Note: requested range exceeds the {fetchRadiusCap}-degree fetch cap - coverage beyond that defaults to sea.");
        }

        progress.Report("Fetching coastline data from OpenStreetMap...");
        var ways = await OverpassClient.FetchCoastlineAsync(
            centerLat - fetchRadius, centerLon - fetchRadius,
            centerLat + fetchRadius, centerLon + fetchRadius, ct);

        progress.Report($"Fetched {ways.Count} coastline ways - classifying land vs sea...");
        return await Task.Run(() => Classify(ways, centerLat, centerLon, radiusDeg, gridSize, progress), ct);
    }

    public static List<(double Lat, double Lon)> Classify(
        List<CoastlineWay> ways, double centerLat, double centerLon, double radiusDeg, int gridSize,
        IProgress<string> progress)
    {
        var factory = new GeometryFactory();
        var lonScale = Math.Cos(centerLat * Math.PI / 180.0) * MetersPerDegree;

        (double X, double Y) ToLocalMeters(double lat, double lon) =>
            ((lon - centerLon) * lonScale, (lat - centerLat) * MetersPerDegree);

        (double Lat, double Lon) ScreenToLonLat(double px, double py)
        {
            var normLon = px / ScreenSize;
            var normLat = (ScreenSize - py) / ScreenSize;
            var lon = centerLon + (normLon * 2.0 * radiusDeg - radiusDeg);
            var lat = centerLat + (normLat * 2.0 * radiusDeg - radiusDeg);
            return (lat, lon);
        }

        // Build the line network in local meters: every coastline way, plus a
        // closed bounding-box ring slightly larger than the configured
        // radius, so every way becomes part of at least one properly closed
        // ring - open-ended ways alone would never polygonize.
        var lineGeoms = new List<Geometry>();
        var segments = new List<(double X1, double Y1, double X2, double Y2)>();

        // Real OSM coastline ways are often vertex-dense at a scale meant for
        // proper cartography (points every few meters, sometimes less) - way
        // finer than a 60x60 grid spanning hundreds of km needs. Left as-is,
        // a handful of long, highly-detailed ways make every downstream
        // overlay operation (polygonize, per-cell intersection) pay for that
        // resolution repeatedly, projecting to many minutes for a full grid.
        //
        // This tolerance must stay small (a handful of meters) regardless of
        // how large the render's cells are - it does NOT scale with cell
        // size. An earlier version used cellSize/10 (hundreds of meters at
        // typical ranges), which seemed reasonable but actually collapsed
        // large, complex regions (e.g. Auckland's harbours/isthmus) into a
        // literal handful of giant, wrongly-shaped faces - at that point the
        // land/sea classification (nearest coastline segment to one interior
        // point) breaks down completely, since "nearest" for a face spanning
        // 100+km can be an unrelated, distant piece of coastline rather than
        // that face's own boundary. A small fixed tolerance still collapses
        // genuine near-duplicate vertices (keeping performance manageable
        // together with the spatial-indexed rasterization below) without
        // altering real topology.
        const double simplifyToleranceMeters = 3.0;

        foreach (var way in ways)
        {
            if (way.Points.Count < 2) continue;

            var rawCoords = new Coordinate[way.Points.Count];
            for (int i = 0; i < way.Points.Count; i++)
            {
                var (x, y) = ToLocalMeters(way.Points[i].Lat, way.Points[i].Lon);
                rawCoords[i] = new Coordinate(x, y);
            }

            var simplified = (LineString)DouglasPeuckerSimplifier.Simplify(factory.CreateLineString(rawCoords), simplifyToleranceMeters);
            var coords = simplified.Coordinates;
            if (coords.Length < 2) continue;

            lineGeoms.Add(simplified);
            for (int i = 0; i + 1 < coords.Length; i++)
                segments.Add((coords[i].X, coords[i].Y, coords[i + 1].X, coords[i + 1].Y));
        }

        // The closing ring exists only so every real way becomes part of a
        // properly closed ring for Polygonizer - it is NOT a real coastline
        // edge, so it must NOT be added to `segments` (deliberately kept out
        // of the nearest-segment classification search below, matching the
        // verified-correct Python reference this was ported from). Getting
        // this wrong is exactly what caused smaller ranges to show no land
        // at all: at a small radius the box edges sit much closer to more of
        // the interior than any real coastline does, so most faces would get
        // "classified" against the box's arbitrary winding direction instead
        // of real data.
        var closeRadiusDeg = radiusDeg * 1.05;
        var (bx0, by0) = ToLocalMeters(centerLat - closeRadiusDeg, centerLon - closeRadiusDeg);
        var (bx1, by1) = ToLocalMeters(centerLat + closeRadiusDeg, centerLon + closeRadiusDeg);
        var boundaryCoords = new[]
        {
            new Coordinate(bx0, by0), new Coordinate(bx1, by0),
            new Coordinate(bx1, by1), new Coordinate(bx0, by1),
            new Coordinate(bx0, by0),
        };
        lineGeoms.Add(factory.CreateLineString(boundaryCoords));

        progress.Report("Building coastline network...");
        var collection = factory.BuildGeometry(lineGeoms);
        var noded = UnaryUnionOp.Union(collection);

        progress.Report("Polygonizing...");
        var polygonizer = new Polygonizer();
        polygonizer.Add(noded);
        var faces = polygonizer.GetPolygons();

        progress.Report($"Classifying {faces.Count} faces...");
        // A single interior point's "nearest segment" is only reliable for
        // small, simple faces. For a large, complex one (a whole harbour
        // system, an isthmus, open ocean spanning 30%+ of the visible area)
        // different points within the SAME connected face can legitimately
        // be nearest to entirely different, unrelated stretches of coastline
        // - confirmed directly: one such face's own interior point tested as
        // sea, while another point deep inside that exact same face tested
        // as land. Voting across many points along the face's own boundary
        // fixes this, but only if those points are (a) actually near real
        // coastline (an edge from the artificial bounding-box ring, or one
        // whose nearest match is only coincidentally close, carries no
        // signal) and (b) offset OFF the boundary line itself - a point
        // exactly on the line one is currently testing gives an
        // essentially noise-driven sign, not a meaningful side. The offset
        // direction (right-of-edge-direction, empirically) matches how
        // NTS's Polygonizer happens to orient its output rings.
        const double insetMeters = 2.0;
        const double confidenceDistMeters = 5.0;
        var confidenceDistSq = confidenceDistMeters * confidenceDistMeters;

        bool ClassifyFace(Polygon face)
        {
            var ring = face.ExteriorRing.Coordinates;
            int landVotes = 0, seaVotes = 0;
            for (int i = 0; i + 1 < ring.Length; i++)
            {
                var a = ring[i];
                var b = ring[i + 1];
                var edx = b.X - a.X;
                var edy = b.Y - a.Y;
                var len = Math.Sqrt(edx * edx + edy * edy);
                if (len == 0) continue;
                var nx = edy / len;
                var ny = -edx / len;
                var testX = (a.X + b.X) / 2 + nx * insetMeters;
                var testY = (a.Y + b.Y) / 2 + ny * insetMeters;

                var cross = NearestSegmentCross(testX, testY, segments, out var distSq);
                if (distSq > confidenceDistSq) continue;
                if (cross > 0) landVotes++; else seaVotes++;
            }

            if (landVotes + seaVotes == 0)
            {
                // No confidently-real edge anywhere on this face's boundary
                // (fully inside the box with nothing nearby) - fall back to
                // the simple interior-point test, same as before.
                var p = face.InteriorPoint;
                return NearestSegmentCross(p.X, p.Y, segments, out _) > 0;
            }

            return landVotes > seaVotes;
        }

        var landFaces = new List<Geometry>();
        foreach (Polygon face in faces.Cast<Polygon>())
        {
            if (ClassifyFace(face))
                landFaces.Add(face);
        }

        // The network/faces/classification above deliberately used a tiny,
        // fixed simplification tolerance (3m) - anything coarser and large,
        // complex regions (a whole harbour system, an isthmus) collapse into
        // a handful of giant, wrongly-shaped faces, breaking the nearest-
        // segment classification test entirely (confirmed: it silently
        // classified almost all of a real Auckland-area test as sea).
        //
        // But that same fine detail makes rasterization - 3600 individual
        // polygon intersections - genuinely expensive if left as-is (an
        // Auckland-area test took ~7 minutes). The fix is to only simplify
        // NOW, per classified face, after land/sea has already been decided
        // correctly: this can only smooth a boundary slightly, it can't flip
        // a face's classification anymore. Each face is simplified on its
        // own (not after unioning - Polygonizer's output never overlaps, so
        // summing independently-simplified per-face intersection areas per
        // cell is still equivalent, and avoids re-unioning a large geometry).
        var cellSizeMeters = (2.0 * radiusDeg * MetersPerDegree) / gridSize;
        var rasterSimplifyToleranceMeters = cellSizeMeters / 10.0;

        var landFaceIndex = new STRtree<Geometry>();
        foreach (var face in landFaces)
        {
            var simplifiedFace = DouglasPeuckerSimplifier.Simplify(face, rasterSimplifyToleranceMeters);
            if (simplifiedFace.IsEmpty) continue;
            landFaceIndex.Insert(simplifiedFace.EnvelopeInternal, simplifiedFace);
        }

        progress.Report("Rasterizing to device grid...");
        var cellPx = (double)ScreenSize / gridSize;
        var results = new List<(double Lat, double Lon)>();

        for (int cy = 0; cy < gridSize; cy++)
        {
            progress.Report($"Rasterizing row {cy}/{gridSize} (cell {cy * gridSize}/{gridSize * gridSize})...");
            for (int cx = 0; cx < gridSize; cx++)
            {
                var corners = new[]
                {
                    ScreenToLonLat(cx * cellPx, cy * cellPx),
                    ScreenToLonLat((cx + 1) * cellPx, cy * cellPx),
                    ScreenToLonLat((cx + 1) * cellPx, (cy + 1) * cellPx),
                    ScreenToLonLat(cx * cellPx, (cy + 1) * cellPx),
                };
                var metersCorners = corners.Select(c => ToLocalMeters(c.Lat, c.Lon)).ToArray();
                var minX = metersCorners.Min(c => c.X);
                var maxX = metersCorners.Max(c => c.X);
                var minY = metersCorners.Min(c => c.Y);
                var maxY = metersCorners.Max(c => c.Y);

                var cellPoly = factory.CreatePolygon(new[]
                {
                    new Coordinate(minX, minY), new Coordinate(maxX, minY),
                    new Coordinate(maxX, maxY), new Coordinate(minX, maxY),
                    new Coordinate(minX, minY),
                });

                var cellArea = cellPoly.Area;
                if (cellArea <= 0) continue;

                double landArea = 0;
                foreach (var candidate in landFaceIndex.Query(cellPoly.EnvelopeInternal))
                    landArea += candidate.Intersection(cellPoly).Area;

                if (landArea / cellArea > 0.5)
                {
                    var (lat, lon) = ScreenToLonLat(cx * cellPx + cellPx / 2, cy * cellPx + cellPx / 2);
                    results.Add((lat, lon));
                }
            }
        }

        progress.Report($"Done - {results.Count} land cells out of {gridSize * gridSize}.");
        return results;
    }

    // Same clamped point-on-segment projection + cross product as
    // CoastlineManager.cpp's PointSegmentDistSq, just in plain doubles since
    // the desktop has no reason to avoid floating point. Also reports the
    // squared distance to that nearest segment, so callers can tell a
    // genuinely-close match from a "nearest available, but still far away"
    // one that carries no real signal.
    private static double NearestSegmentCross(double px, double py, List<(double X1, double Y1, double X2, double Y2)> segments, out double bestDistSq)
    {
        bestDistSq = double.MaxValue;
        double bestCross = 0;

        foreach (var (x1, y1, x2, y2) in segments)
        {
            var dx = x2 - x1;
            var dy = y2 - y1;
            var lenSq = dx * dx + dy * dy;

            double closestX, closestY;
            if (lenSq == 0)
            {
                closestX = x1;
                closestY = y1;
            }
            else
            {
                var t = ((px - x1) * dx + (py - y1) * dy) / lenSq;
                t = Math.Clamp(t, 0.0, 1.0);
                closestX = x1 + t * dx;
                closestY = y1 + t * dy;
            }

            var ddx = px - closestX;
            var ddy = py - closestY;
            var distSq = ddx * ddx + ddy * ddy;

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestCross = dx * (py - y1) - dy * (px - x1);
            }
        }

        return bestCross;
    }
}
