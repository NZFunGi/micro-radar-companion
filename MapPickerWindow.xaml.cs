using System.Globalization;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace MicroRadarCompanion;

// A modal latitude/longitude picker, opened from MainWindow's "Pick on
// map..." button - same "dialog opened by a button" shape as the color
// picker's WinForms.ColorDialog in MainWindow's ChangeColor_Click, just a WPF
// Window instead since there's no built-in map equivalent to reach for.
//
// Hosts a small Leaflet map (loaded straight from the unpkg CDN in
// BuildMapHtml - no offline requirement, so there's no need to vendor/embed
// the library) inside a WebView2 control. Clicking the map or dragging its
// marker posts the new point back to this window via WebView2's JS-to-host
// messaging (window.chrome.webview.postMessage); ResultLat/ResultLon expose
// whatever was last picked once the user confirms with "Use this location".
public partial class MapPickerWindow : Window
{
    private readonly double seedLat;
    private readonly double seedLon;
    private readonly double seedRadiusKm;

    // Seeded from the constructor so "Use this location" still does
    // something sensible (returns the original point unchanged) even if the
    // user never actually clicks the map.
    private double pickedLat;
    private double pickedLon;

    public double ResultLat { get; private set; }
    public double ResultLon { get; private set; }

    public MapPickerWindow(double lat, double lon, double radiusKm)
    {
        InitializeComponent();

        seedLat = lat;
        seedLon = lon;
        seedRadiusKm = radiusKm;
        pickedLat = lat;
        pickedLon = lon;

        Loaded += MapPickerWindow_Loaded;
    }

    private async void MapPickerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await MapWebView.EnsureCoreWebView2Async();
            MapWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            MapWebView.CoreWebView2.NavigateToString(BuildMapHtml(seedLat, seedLon, seedRadiusKm));
        }
        catch (Exception ex)
        {
            // Most likely cause: the WebView2 Runtime isn't installed (it
            // ships with Windows 10/11 by default, but isn't guaranteed on
            // every machine) - see the companion README's FAQ entry for this.
            StatusText.Text = $"Map failed to load: {ex.Message}";
            UseLocationButton.IsEnabled = false;
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            pickedLat = doc.RootElement.GetProperty("lat").GetDouble();
            pickedLon = doc.RootElement.GetProperty("lon").GetDouble();
            StatusText.Text = $"{pickedLat:F6}, {pickedLon:F6}";
        }
        catch (Exception ex)
        {
            // A malformed message from the page shouldn't be fatal - just
            // keep whatever point was picked last.
            StatusText.Text = $"Ignored malformed map message: {ex.Message}";
        }
    }

    private void UseLocation_Click(object sender, RoutedEventArgs e)
    {
        ResultLat = pickedLat;
        ResultLon = pickedLon;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    // Self-contained page: Leaflet is pulled from the unpkg CDN (exact
    // version/integrity from Leaflet's own "Quick Start" docs at
    // https://leafletjs.com/download.html), tiles from the standard OSM
    // tile server (attribution required and included below, per OSM's tile
    // usage policy - same data source this app already uses for coastline
    // generation via OverpassClient/CoastlineGenerator).
    //
    // Placeholder tokens (__LAT__ etc.) are replaced below rather than using
    // C# string interpolation, since Leaflet's own tile URL template
    // ({s}/{z}/{x}/{y}) uses the exact same brace syntax and would otherwise
    // have to be escaped throughout the whole HTML/CSS/JS block.
    private static string BuildMapHtml(double lat, double lon, double radiusKm)
    {
        var radiusMeters = radiusKm * 1000.0;

        const string template = """
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8" />
            <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css"
                  integrity="sha256-p4NxAoJBhIIN+hmNHrzRCf9tD/miZyoHS5obTRR9BMY=" crossorigin="" />
            <style>
                html, body, #map { height: 100%; margin: 0; padding: 0; }
            </style>
            </head>
            <body>
            <div id="map"></div>
            <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"
                    integrity="sha256-20nQCchB9co0qIjJZRGuk2/Z9VM+kNiyxNV1lvTlZBo=" crossorigin=""></script>
            <script>
                var lat = __LAT__, lon = __LON__, radiusMeters = __RADIUS_M__;
                var map = L.map('map').setView([lat, lon], 10);

                L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
                    maxZoom: 19,
                    attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
                }).addTo(map);

                var marker = L.marker([lat, lon], { draggable: true }).addTo(map);
                var circle = L.circle([lat, lon], { radius: radiusMeters, color: '#3388ff', fill: false }).addTo(map);

                if (radiusMeters > 0) {
                    map.fitBounds(circle.getBounds(), { padding: [20, 20] });
                }

                function post(point) {
                    marker.setLatLng(point);
                    circle.setLatLng(point);
                    window.chrome.webview.postMessage({ lat: point.lat, lon: point.lng });
                }

                map.on('click', function (e) { post(e.latlng); });
                marker.on('dragend', function () { post(marker.getLatLng()); });
            </script>
            </body>
            </html>
            """;

        return template
            .Replace("__LAT__", lat.ToString(CultureInfo.InvariantCulture))
            .Replace("__LON__", lon.ToString(CultureInfo.InvariantCulture))
            .Replace("__RADIUS_M__", radiusMeters.ToString(CultureInfo.InvariantCulture));
    }
}
