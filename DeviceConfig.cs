using System.Text.Json;
using System.Text.Json.Serialization;

namespace MicroRadarCompanion;

// Matches the one-line JSON GET_CONFIG replies with on the firmware side
// (see SerialCommandManager::HandleCommand):
//   {"lat":-36.485789,"lon":174.439981,"radius":1.5,
//    "openskyId":"abc123","openskySecret":"********",
//    "toggles":{"scanline":true,"infotext":true,"triangle":true,"coastline":true},
//    "colors":{"SEA":"B0C4D2",...}}
public class DeviceConfig
{
    [JsonPropertyName("lat")]
    public double Latitude { get; set; }

    [JsonPropertyName("lon")]
    public double Longitude { get; set; }

    [JsonPropertyName("radius")]
    public double Radius { get; set; }

    [JsonPropertyName("openskyId")]
    public string OpenskyId { get; set; } = "";

    // Always a string of '*' matching the stored secret's length (or empty
    // if unset) - the device never sends the real secret back over serial,
    // matching how the on-device web config page masks it too. Round-trip
    // this value unchanged in SET_OPENSKY_AUTH to leave the stored secret
    // alone; only send something else if the user actually typed a new one.
    [JsonPropertyName("openskySecret")]
    public string OpenskySecret { get; set; } = "";

    // The currently-connected network, live from the device's WiFi.SSID() -
    // not a stored/pending value, and never a password (there's no way to
    // read one back - SET_WIFI always takes a fresh SSID+password pair).
    [JsonPropertyName("wifiSsid")]
    public string WifiSsid { get; set; } = "";

    [JsonPropertyName("toggles")]
    public DeviceToggles Toggles { get; set; } = new();

    [JsonPropertyName("colors")]
    public Dictionary<string, string> Colors { get; set; } = new();

    public static DeviceConfig Parse(string json) =>
        JsonSerializer.Deserialize<DeviceConfig>(json)
        ?? throw new JsonException("GET_CONFIG returned an empty/invalid response.");
}

// The four display toggles the device's web config page also exposes - see
// the "toggles" object in GET_CONFIG's reply above. Each maps 1:1 to a
// SET_TOGGLE <name> <true|false> command, where <name> is the JSON property
// name in lowercase (e.g. Scanline -> "scanline").
public class DeviceToggles
{
    [JsonPropertyName("scanline")]
    public bool Scanline { get; set; } = true;

    [JsonPropertyName("infotext")]
    public bool Infotext { get; set; } = true;

    [JsonPropertyName("triangle")]
    public bool Triangle { get; set; } = true;

    [JsonPropertyName("coastline")]
    public bool Coastline { get; set; } = true;
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
