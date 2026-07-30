using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicroRadarCompanion;

// Matches the one-line JSON GET_CONFIG replies with on the firmware side
// (see SerialCommandManager::HandleCommand):
//   {"lat":-36.485789,"lon":174.439981,"radius":1.5,"colors":{"SEA":"B0C4D2",...}}
public class DeviceConfig
{
    [JsonPropertyName("lat")]
    public double Latitude { get; set; }

    [JsonPropertyName("lon")]
    public double Longitude { get; set; }

    [JsonPropertyName("radius")]
    public double Radius { get; set; }

    [JsonPropertyName("colors")]
    public Dictionary<string, string> Colors { get; set; } = new();

    public static DeviceConfig Parse(string json) =>
        JsonSerializer.Deserialize<DeviceConfig>(json)
        ?? throw new JsonException("GET_CONFIG returned an empty/invalid response.");
}

// The fixed set of color keys the firmware understands (must match
// ColorConfig::Key / kEntries in ColorConfig.cpp), plus a friendly label for
// the UI.
public static class ColorKeys
{
    public static readonly (string Key, string Label)[] All =
    [
        ("SEA", "Sea"),
        ("LAND", "Land"),
        ("RADAR", "Radar rings"),
        ("CAT_LIGHT", "Aircraft: Light / small"),
        ("CAT_LARGE", "Aircraft: Large / high vortex"),
        ("CAT_HEAVY", "Aircraft: Heavy"),
        ("CAT_ROTOR", "Aircraft: Rotorcraft"),
        ("CAT_GLIDER", "Aircraft: Glider / ultralight"),
        ("CAT_UNKNOWN", "Aircraft: Unknown category"),
        ("RANGE_LABEL", "Range label"),
    ];
}
