using System.ComponentModel;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace MicroRadarCompanion;

// One row in the color list - a device color key (e.g. "CAT_HEAVY"), its
// friendly label, and the currently-known swatch color. Implements
// INotifyPropertyChanged so the swatch rectangle updates the instant a new
// color is picked, without needing to rebuild the whole list.
public class ColorItem : INotifyPropertyChanged
{
    public required string Key { get; init; }
    public required string Label { get; init; }

    private Color color;
    public Color Color
    {
        get => color;
        set
        {
            color = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Brush)));
            // Keep the editable hex box in sync whenever the color changes
            // via the picker dialog or an initial load - but HexInput itself
            // stays a separate, freely-editable property so typing a new
            // value here doesn't fight the binding before "Set" is clicked.
            HexInput = HexRgb;
        }
    }

    public Brush Brush => new SolidColorBrush(Color);

    public string HexRgb => $"{Color.R:X2}{Color.G:X2}{Color.B:X2}";

    private string hexInput = "";
    public string HexInput
    {
        get => hexInput;
        set
        {
            hexInput = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HexInput)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
