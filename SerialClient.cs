using System.Globalization;
using System.IO.Ports;
using System.Text;

namespace MicroRadarCompanion;

// Thin wrapper around System.IO.Ports.SerialPort matching the device's line
// protocol (see SerialCommandManager.cpp on the firmware side): one command
// per line, one reply line back. Kept deliberately simple - no framing,
// checksums, or binary payloads, since this only ever talks to one device
// over a direct USB-CDC link, and plain text keeps it debuggable by hand
// through any regular serial terminal too.
public class SerialClient : IDisposable
{
    private SerialPort? port;

    // Serializes SendCommandAsync calls. Without this, clicking through
    // several color rows quickly enough (each triggers an async command,
    // and the UI thread is free to fire another before the first reply
    // arrives) let two commands' writes/reads interleave on the same port,
    // with no guarantee which reply got read by which caller - the actual
    // fix for the "colors ended up swapped" report, not a firmware bug.
    private readonly SemaphoreSlim commandLock = new(1, 1);

    public bool IsConnected => port is { IsOpen: true };

    public static string[] GetAvailablePorts() => SerialPort.GetPortNames();

    public void Connect(string portName)
    {
        Disconnect();

        port = new SerialPort(portName, 115200)
        {
            NewLine = "\n",
            ReadTimeout = 5000,
            WriteTimeout = 3000,
        };
        port.Open();

        // The device resets when a new USB-CDC connection opens (same as
        // plugging into any Arduino-family board), so give it a moment to
        // finish booting before the first command is sent.
        Thread.Sleep(2000);
        port.DiscardInBuffer();
    }

    public void Disconnect()
    {
        if (port == null) return;
        if (port.IsOpen) port.Close();
        port.Dispose();
        port = null;
    }

    // The device emits its own log lines out-of-band, independent of
    // whatever command is in flight - WiFiManager's "*wm:..." messages,
    // "[INFO]"/"[WARN]"/"[ERROR]" app logs (route lookups, coastline
    // fetches, etc.). These are most likely to race ahead of a reply right
    // after a fresh boot or WiFi reconnect, when the device is still busy.
    // A real protocol reply always starts with one of these markers, so
    // anything else read here is noise to skip past, not the answer -
    // otherwise a stray "*wm:AutoConnect" can get handed to a JSON parser
    // and blow up as "'*' is an invalid start of a value".
    private static bool LooksLikeReply(string line) =>
        line.StartsWith("OK") || line.StartsWith("ERR") || line == "PONG" ||
        line.StartsWith("{") || line.StartsWith("PROGRESS");

    private string ReadReplyLine()
    {
        while (true)
        {
            var line = port!.ReadLine();
            if (LooksLikeReply(line)) return line;
        }
    }

    // Sends one command line and waits for the actual reply line the device
    // sends back (OK / ERR ... / PONG / a JSON blob for GET_CONFIG),
    // skipping over any incidental log lines that arrive first.
    public async Task<string> SendCommandAsync(string command)
    {
        if (port is not { IsOpen: true }) throw new InvalidOperationException("Not connected to a device.");

        await commandLock.WaitAsync();
        try
        {
            port.DiscardInBuffer();
            await Task.Run(() => port.WriteLine(command));
            return await Task.Run(ReadReplyLine);
        }
        finally
        {
            commandLock.Release();
        }
    }

    // Must match SerialCommandManager::COASTLINE_ACK_INTERVAL on the
    // firmware side - each chunk sent here should correspond to exactly one
    // "PROGRESS"/"OK" reply from the device.
    private const int CoastlineChunkSize = 100;

    // Sends COASTLINE_BEGIN, then every point line in chunks, waiting for the
    // device's ack after each chunk before sending the next (see
    // SerialCommandManager::HandleCoastlineDataLine's "PROGRESS" acks).
    // Originally this wrote the whole batch in one burst and just waited for
    // a single final reply - that overran the device's small USB-CDC receive
    // buffer whenever loop() was momentarily busy (an HTTP call, a render)
    // while the burst arrived, silently dropping bytes and wedging the
    // device in a permanent "still receiving" state. Chunking with acks
    // means this client is only ever a bounded amount ahead of what the
    // device has actually confirmed processing, regardless of timing.
    public async Task<string> SendCoastlineDataAsync(
        IReadOnlyList<(double Lat, double Lon)> points, double genLat, double genLon, double genRad,
        IProgress<string>? progress = null)
    {
        if (port is not { IsOpen: true }) throw new InvalidOperationException("Not connected to a device.");

        await commandLock.WaitAsync();
        try
        {
            port.DiscardInBuffer();

            var beginCmd = $"COASTLINE_BEGIN {points.Count} " +
                           $"{genLat.ToString(CultureInfo.InvariantCulture)} " +
                           $"{genLon.ToString(CultureInfo.InvariantCulture)} " +
                           $"{genRad.ToString(CultureInfo.InvariantCulture)}";
            await Task.Run(() => port.WriteLine(beginCmd));
            var beginReply = await Task.Run(ReadReplyLine);
            if (!beginReply.StartsWith("OK")) return beginReply;

            var originalWriteTimeout = port.WriteTimeout;
            var originalReadTimeout = port.ReadTimeout;
            // Generous per-chunk timeout - the device might be mid-HTTP-call
            // when a chunk arrives, but it should never take this long to
            // catch up and ack once it does.
            port.WriteTimeout = 10000;
            port.ReadTimeout = 10000;
            try
            {
                for (int i = 0; i < points.Count; i += CoastlineChunkSize)
                {
                    var end = Math.Min(i + CoastlineChunkSize, points.Count);
                    var sb = new StringBuilder();
                    for (int j = i; j < end; j++)
                    {
                        var latMicro = (long)Math.Round(points[j].Lat * 1_000_000);
                        var lonMicro = (long)Math.Round(points[j].Lon * 1_000_000);
                        sb.Append(latMicro).Append(',').Append(lonMicro).Append('\n');
                    }

                    await Task.Run(() => port.Write(sb.ToString()));
                    progress?.Report($"Sent {end}/{points.Count} coastline points...");

                    var reply = await Task.Run(ReadReplyLine);
                    if (reply.StartsWith("OK") || reply.StartsWith("ERR")) return reply;
                    // otherwise it's "PROGRESS <n>" - just move on to the next chunk
                }

                // Every chunk should have gotten an ack above, and the chunk
                // that completes the count always gets "OK ..." rather than
                // "PROGRESS" - so this is just a safety net, not the normal path.
                return await Task.Run(ReadReplyLine);
            }
            finally
            {
                port.WriteTimeout = originalWriteTimeout;
                port.ReadTimeout = originalReadTimeout;
            }
        }
        finally
        {
            commandLock.Release();
        }
    }

    public void Dispose()
    {
        Disconnect();
        commandLock.Dispose();
    }
}
