using System.Net.Http;
using System.Text.Json;

namespace MicroRadarCompanion;

// One real-world coastline way, as a sequence of (lon, lat) points in the
// order OpenStreetMap stores it (land on the left, sea on the right of the
// node order - see CoastlineGenerator for how that's actually used).
public class CoastlineWay
{
    public required List<(double Lon, double Lat)> Points { get; init; }
}

// Fetches raw coastline geometry from the public Overpass API - the same
// data source and query shape the firmware's own live-fetch fallback uses
// (see CoastlineManager::FetchCoastlineWays), but running on a desktop with
// a real network stack and no memory ceiling, so the response is just
// deserialized whole rather than streamed/capped.
public static class OverpassClient
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        // Overpass rejects requests with no User-Agent at all (default
        // HttpClient sends none) - identify ourselves, as its usage policy
        // asks of any client.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MicroRadarCompanion/1.0");
        return client;
    }

    public static async Task<List<CoastlineWay>> FetchCoastlineAsync(double south, double west, double north, double east, CancellationToken ct)
    {
        var bbox = $"{south.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
                   $"{west.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
                   $"{north.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}," +
                   $"{east.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}";

        var query = $"[out:json][timeout:100];way[\"natural\"=\"coastline\"]({bbox});out geom({bbox});";

        using var content = new StringContent(query);
        using var response = await Http.PostAsync("https://overpass-api.de/api/interpreter", content, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var ways = new List<CoastlineWay>();
        if (!doc.RootElement.TryGetProperty("elements", out var elements)) return ways;

        foreach (var element in elements.EnumerateArray())
        {
            if (!element.TryGetProperty("geometry", out var geometry)) continue;

            var points = new List<(double Lon, double Lat)>();
            foreach (var node in geometry.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue; // null entries for missing nodes
                if (!node.TryGetProperty("lat", out var latProp) || !node.TryGetProperty("lon", out var lonProp)) continue;
                points.Add((lonProp.GetDouble(), latProp.GetDouble()));
            }

            if (points.Count >= 2) ways.Add(new CoastlineWay { Points = points });
        }

        return ways;
    }
}
