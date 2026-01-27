// DEPRECATED: UDP/RVF receiver
//
// This file implements a UDP receiver for RVF-formatted AVTP payloads. UDP/RVFU support
// is preserved here for reference and potential future re-enablement, but the application
// disables this path by default and does not start the receiver unless explicitly enabled
// via settings. The active AVTP capture path (Ethernet via SharpPcap) remains the
// recommended approach.

using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
namespace VideoStreamPlayer;

public sealed record RvfChunk(
    ushort Width,
    ushort Height,
    ushort LineNumber1Based,
    byte NumLines,
    bool EndFrame,
    uint FrameId,
    uint Seq,
    byte[] Payload);

public sealed class RvfUdpReceiver : IDisposable
{
    private readonly UdpClient _udp;

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "udp_rx.log");

    public event Action<RvfChunk>? OnChunk;

    public RvfUdpReceiver(int port = RvfProtocol.DefaultPort)
    {
        _udp = new UdpClient(port); // bind pe 0.0.0.0:50070
        _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        static void LogLine(string line)
        {
            try
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {line}\r\n");
            }
            catch { }
        }

        static int FindMagic(byte[] buf)
        {
            // search for "RVFU" in the first 64 bytes (tolerant to padding)
            int max = Math.Min(buf.Length - 4, 64);
            for (int i = 0; i <= max; i++)
            {
                if (buf[i] == (byte)'R' && buf[i + 1] == (byte)'V' && buf[i + 2] == (byte)'F' && buf[i + 3] == (byte)'U')
                    return i;
            }
            return -1;
        }

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try
            {
                res = await _udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }

            var buf = res.Buffer;

            // log RAW (so we know something is coming in)
            try
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] RX datagram len={buf.Length} from {res.RemoteEndPoint}\r\n");
            }
            catch { }


            // Delegate UDP/RVF parsing to shared AVTP parser.
            if (!AvtpRvfParser.TryParseRvfUdp(buf, out var chunk))
            {
                LogLine("DROP: failed to parse RVFU payload");
                continue;
            }

            try
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] RVFU udp parsed w={chunk.Width} h={chunk.Height} line={chunk.LineNumber1Based} num={chunk.NumLines} end={chunk.EndFrame} frameId={chunk.FrameId} seq={chunk.Seq} payloadLen={chunk.Payload.Length}\r\n");
            }
            catch { }

            // Version check is implicit in parsing; deliver chunk
            OnChunk?.Invoke(chunk);
        }
    }


    public void Dispose() => _udp.Dispose();
}
