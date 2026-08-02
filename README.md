<h1 align=center>
  🖥️ Micro Radar Companion
</h1>
<h6 align=center>
  a Windows desktop app for configuring your <a href="https://github.com/NZFunGi/micro-radar">Micro Radar</a> over USB
</h6>
<p align=center>
  <img src="docs/app-screenshot.png" alt="Micro Radar Companion app window" width="480"/>
</p>
<p align=center>
  <a href="#what-it-does">WHAT IT DOES</a> - <a href="#getting-started">GETTING STARTED</a> - <a href="#usage">USAGE</a> - <a href="#faq">FAQ</a>
</p>

## What it does

The Micro Radar device stores its color palette, location, and range in flash, and can be reconfigured live over its USB serial connection without a firmware rebuild. This app is the easiest way to do that:

- **Live color pickers** for the sea, land, radar rings, and each aircraft category (light/large/heavy/rotorcraft/glider/unknown) - changes apply on the device's very next drawn frame, no restart needed. Pick from a native color dialog or type a hex code directly.
- **Location & range** - edit latitude, longitude, and radius (shown in km), or click **Pick on map...** to set latitude/longitude visually on an interactive OpenStreetMap map instead of typing coordinates.
- **One-click coastline regeneration** - fetches real coastline data from OpenStreetMap for whatever location/range you set, classifies land vs sea, and pushes the result to the device. This is what actually lets you point the radar anywhere in the world and get an accurate coastline overlay, not just the one location baked into the firmware.
- **OpenSky API credentials & display toggles** - set the Client ID/Secret used for OpenSky requests (this is what unlocks the much larger authenticated request budget - see the main firmware README's FAQ on route info), and flip the radar sweep / aircraft info / directional aircraft / coastline toggles. Applying either restarts the device, since both are only read once at boot.

## Getting Started

### Option A: Download a build

Check the [Releases](../../releases) page for a ready-to-run `MicroRadarCompanion.exe` - it's fully self-contained (the whole .NET runtime is bundled in), so it runs on a Windows machine that's never had .NET installed. Just download and double-click it.

### Option B: Build from source

You'll need the [.NET 8 SDK](https://dotnet.microsoft.com/download). Then, from the project folder:

```
dotnet build
```

to build and run via your IDE, or:

```
dotnet publish -c Release
```

to produce a single-file executable at `bin\Release\net8.0-windows\win-x64\publish\MicroRadarCompanion.exe`.

## Usage

1. Plug your Micro Radar device into a USB port.
2. Open the app, pick the correct COM port from the dropdown (use **Refresh** if it doesn't show up right away), and click **Connect**.
3. Once connected, the app reads the device's current colors, location, and range.
4. **To change colors**: click **Change...** next to any color to open a picker, or type a 6-digit hex code (e.g. `B0C4D2`) into the box and click **Set** / press Enter.
5. **To change location or range**: edit the fields directly, or click **Pick on map...** to open an interactive map - click anywhere (or drag the marker) to move the pin, the blue circle shows your current range for reference, then click **Use this location** to fill in the Latitude/Longitude fields. Either way, click **Apply & Regenerate Coastline** to actually push the change: this fetches real coastline data for the new location/range from OpenStreetMap, pushes it to the device, then restarts the device to apply everything. Depending on range and coastline complexity, this can take anywhere from a few seconds to about a minute - the log line at the bottom shows progress.
6. **To set OpenSky credentials or display toggles**: edit the Client ID/Secret and/or the checkboxes in the **OpenSky API & display options** box, then click **Apply & Restart**. The Client Secret box shows asterisks once a secret is already set on the device - leave it as-is to keep that secret, or clear it and type a new one to replace it. This restarts the device to apply the change.

## FAQ

> The port dropdown is empty, or my device doesn't show up

Click **Refresh** - the device may have been plugged in after the app started. If it's still missing, check Windows Device Manager under "Ports (COM & LPT)" to confirm the device is recognized at all.
<br/><br/>

> "Connect failed: The operation has timed out"

The device resets when a new USB connection opens (same as plugging into any Arduino-family board) and can take a few seconds to finish booting, connect to WiFi, and settle down before it's ready to answer. Just try **Connect** again.
<br/><br/>

> "Apply & Regenerate Coastline" is stuck on "Fetching coastline data..." for a long time, or fails - e.g. "Failed to apply location/coastline: Response status code does not indicate success: 504 (Gateway Timeout)"

This step calls the public [Overpass API](https://overpass-api.de) (OpenStreetMap's query service), which is a free, best-effort service with no guaranteed uptime or rate limit - a `504` means their server (or a gateway in front of it) was briefly overwhelmed, not that anything's wrong with your chosen location or the app itself. This can happen even on your very first attempt for a given location (a dense, complex coastline like a big harbour or bay can just take a while to process), not only when making several requests in a row. Just click **Apply & Regenerate Coastline** again - it usually goes through on the next try.
<br/><br/>

> The "Pick on map..." window is blank, or says "Map failed to load"

This feature needs the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) to render the map - it ships with Windows 10/11 by default (it's the same component Edge uses), so this is usually only a problem on an older or heavily stripped-down Windows install. Install the "Evergreen Bootstrapper" from that link and try again. It also needs an internet connection, same as coastline regeneration - the map tiles come from OpenStreetMap.
<br/><br/>

> How accurate is the generated coastline?

It's built from real OpenStreetMap coastline data, so it's as accurate as OpenStreetMap is for your area - which for most populated coastlines is very good. The device only has a 60x60 grid to render into regardless of range, so at very large ranges (100s of km) fine detail like small islands or narrow inlets may not show individually.
<br/><br/>

> Do I need an OpenSky account for this app specifically?

No - an OpenSky account is only needed if you want the much larger authenticated request budget for aircraft position polling and route (`O:`/`D:`) lookups; the radar works fine anonymously, just with a smaller daily quota. If you do have one, its Client ID/Secret can be set either through this app's **OpenSky API & display options** box, or the device's own web config page at `http://microradar.local` - both write to the same place on the device, so use whichever's more convenient.
<br/><br/>

> Can I run this on macOS or Linux?

Not currently - it's a Windows-only WPF application. The device itself and its web config page work from any OS; this app is specifically for the extra features (color pickers, coastline regeneration) that aren't available through the web config page.

## Notes

> Built to work alongside [NZFunGi/micro-radar](https://github.com/NZFunGi/micro-radar), a fork of [AnthonySturdy/micro-radar](https://github.com/AnthonySturdy/micro-radar)
