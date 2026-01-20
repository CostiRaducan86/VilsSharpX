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
            // caută "RVFU" în primii 64 bytes (tolerant la padding)
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

            // log RAW (ca să știm că intră ceva)
            try
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] RX datagram len={buf.Length} from {res.RemoteEndPoint}\r\n");
            }
            catch { }


            if (buf.Length < RvfProtocol.HeaderSize)
            {
                LogLine($"DROP: too short for header (len={buf.Length} < {RvfProtocol.HeaderSize})");
                continue;
            }

            int m = FindMagic(buf);
            if (m < 0)
            {
                int n = Math.Min(16, buf.Length);
                var head = new System.Text.StringBuilder(n * 3);
                for (int i = 0; i < n; i++) head.AppendFormat("{0:X2} ", buf[i]);
                LogLine($"DROP: no RVFU magic in first 64 bytes. head={head}");
                continue;
            }

            int o = m + 4;
            if (buf.Length < o + (RvfProtocol.HeaderSize - 4))
            {
                LogLine("DROP: header truncated after magic");
                continue;
            }

            byte ver = buf[o++];
            ushort w = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(o)); o += 2;
            ushort h = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(o)); o += 2;
            ushort line = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(o)); o += 2;
            byte numLines = buf[o++];
            bool endFrame = buf[o++] != 0;
            uint frameId = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(o)); o += 4;
            uint seq = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(o)); o += 4;

            int payloadLen = numLines * w;

            try
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] RVFU@{m} ver={ver} w={w} h={h} line={line} num={numLines} end={endFrame} frameId={frameId} seq={seq} payloadLen={payloadLen}\r\n");
            }
            catch { }

            if (ver != RvfProtocol.Version)
            {
                LogLine($"DROP: version mismatch (ver={ver} expected={RvfProtocol.Version})");
                continue;
            }

            if (payloadLen <= 0 || payloadLen > 200000)
            {
                LogLine($"DROP: payloadLen out of range (payloadLen={payloadLen})");
                continue;
            }

            if (buf.Length < o + payloadLen)
            {
                LogLine($"DROP: datagram too short for payload (len={buf.Length} need={o + payloadLen})");
                continue;
            }

            var payload = new byte[payloadLen];
            Buffer.BlockCopy(buf, o, payload, 0, payloadLen);

            OnChunk?.Invoke(new RvfChunk(w, h, line, numLines, endFrame, frameId, seq, payload));
        }
    }


    public void Dispose() => _udp.Dispose();
}
