using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using MessageBox = System.Windows.MessageBox;
using WinForms = System.Windows.Forms;

namespace MicroRadarCompanion;

public partial class MainWindow : Window
{
    // The device stores/sends radius in degrees (that's the unit its
    // projection math actually uses) - this app only converts to km at the
    // UI boundary, since "degrees" isn't a meaningful unit for a person to
    // type in. Matches the same constant used on the firmware/web-config side.
    private const double KmPerDegree = 111.32;

    private readonly SerialClient serial = new();
    private readonly ObservableCollection<ColorItem> colors = new();

    public MainWindow()
    {
        InitializeComponent();
        ColorsList.ItemsSource = colors;
        RefreshPorts();
    }

    private void RefreshPorts()
    {
        PortComboBox.ItemsSource = SerialClient.GetAvailablePorts();
        if (PortComboBox.Items.Count > 0) PortComboBox.SelectedIndex = 0;
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (serial.IsConnected)
        {
            serial.Disconnect();
            ConnectButton.Content = "Connect";
            StatusText.Text = "Not connected";
            colors.Clear();
            return;
        }

        if (PortComboBox.SelectedItem is not string portName)
        {
            Log("Pick a COM port first.");
            return;
        }

        try
        {
            StatusText.Text = "Connecting...";
            await Task.Run(() => serial.Connect(portName));

            var reply = await serial.SendCommandAsync("GET_CONFIG");
            var config = DeviceConfig.Parse(reply);

            LatitudeBox.Text = config.Latitude.ToString(CultureInfo.InvariantCulture);
            LongitudeBox.Text = config.Longitude.ToString(CultureInfo.InvariantCulture);
            RadiusBox.Text = (config.Radius * KmPerDegree).ToString("F1", CultureInfo.InvariantCulture);

            colors.Clear();
            foreach (var (key, label) in ColorKeys.All)
            {
                var hex = config.Colors.GetValueOrDefault(key, "808080");
                colors.Add(new ColorItem { Key = key, Label = label, Color = ParseHexColor(hex) });
            }

            ConnectButton.Content = "Disconnect";
            StatusText.Text = $"Connected on {portName}";
            Log("Loaded current device config.");
        }
        catch (Exception ex)
        {
            serial.Disconnect();
            StatusText.Text = "Not connected";
            Log($"Connect failed: {ex.Message}");
        }
    }

    private async void ChangeColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ColorItem item }) return;
        if (!serial.IsConnected)
        {
            Log("Connect to the device first.");
            return;
        }

        using var dialog = new WinForms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(item.Color.R, item.Color.G, item.Color.B),
            FullOpen = true,
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;

        var picked = dialog.Color;
        var hex = $"{picked.R:X2}{picked.G:X2}{picked.B:X2}";
        await ApplyHexColorAsync(item, hex);
    }

    private async void SetHexColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ColorItem item }) return;
        await ApplyHexColorAsync(item, item.HexInput);
    }

    private async void HexInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if (sender is not System.Windows.Controls.TextBox { Tag: ColorItem item }) return;
        await ApplyHexColorAsync(item, item.HexInput);
    }

    private async Task ApplyHexColorAsync(ColorItem item, string hex)
    {
        if (!serial.IsConnected)
        {
            Log("Connect to the device first.");
            return;
        }

        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
        {
            Log($"\"{hex}\" isn't a valid 6-digit hex color (e.g. B0C4D2).");
            return;
        }

        try
        {
            var reply = await serial.SendCommandAsync($"SET_COLOR {item.Key} {hex}");
            if (reply.StartsWith("OK"))
            {
                item.Color = ParseHexColor(hex);
                Log($"{item.Label} -> #{hex}");
            }
            else
            {
                Log($"Device rejected color change: {reply}");
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to send color: {ex.Message}");
        }
    }

    // Matches CoastlineManager::GRID_SIZE on the firmware side - the
    // regenerated data is only meaningful if it's rasterized to the exact
    // same grid the device renders with.
    private const int DeviceGridSize = 60;

    private async void ApplyLocation_Click(object sender, RoutedEventArgs e)
    {
        if (!serial.IsConnected)
        {
            Log("Connect to the device first.");
            return;
        }

        if (!double.TryParse(LatitudeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(LongitudeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) ||
            !double.TryParse(RadiusBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var radKm))
        {
            Log("Latitude/longitude/radius must be numbers.");
            return;
        }

        var radDeg = radKm / KmPerDegree;

        var confirmed = MessageBox.Show(
            "This fetches real coastline data for this location/range from OpenStreetMap, pushes it to the device, " +
            "then restarts the device to apply everything. Continue?",
            "Regenerate coastline & restart", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmed != MessageBoxResult.Yes) return;

        ApplyLocationButton.IsEnabled = false;
        var progress = new Progress<string>(Log);

        try
        {
            var points = await CoastlineGenerator.GenerateAsync(lat, lon, radDeg, DeviceGridSize, progress, CancellationToken.None);

            // Location must be applied on the device BEFORE the coastline
            // push - StoreCoastlinePoints only previews the new data live if
            // it matches what the device currently thinks its location/radius
            // is (see CoastlineManager::SetLocation / StoreCoastlinePoints).
            var setReply = await serial.SendCommandAsync(
                $"SET_LOCATION {lat.ToString(CultureInfo.InvariantCulture)} {lon.ToString(CultureInfo.InvariantCulture)} {radDeg.ToString(CultureInfo.InvariantCulture)}");
            if (!setReply.StartsWith("OK"))
            {
                Log($"Device rejected location: {setReply}");
                return;
            }

            var coastlineReply = await serial.SendCoastlineDataAsync(points, lat, lon, radDeg, progress);
            if (!coastlineReply.StartsWith("OK"))
            {
                Log($"Device rejected coastline data: {coastlineReply}");
                return;
            }

            await serial.SendCommandAsync("RESTART");
            Log($"Applied {points.Count}-point coastline and new range - device is restarting.");
            serial.Disconnect();
            ConnectButton.Content = "Connect";
            StatusText.Text = "Not connected (device restarting)";
        }
        catch (Exception ex)
        {
            Log($"Failed to apply location/coastline: {ex.Message}");
        }
        finally
        {
            ApplyLocationButton.IsEnabled = true;
        }
    }

    private static Color ParseHexColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6) return Colors.Gray;
        var r = byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromRgb(r, g, b);
    }

    private void Log(string message) => LogText.Text = message;

    protected override void OnClosed(EventArgs e)
    {
        serial.Dispose();
        base.OnClosed(e);
    }
}
