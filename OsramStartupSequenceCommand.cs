using System;
using System.Collections.Generic;
using System.Linq;
using SharpPcap;
using SharpPcap.LibPcap;

namespace VilsSharpX;

/// <summary>
/// Uploads an OSRAM Direct Control start-up sequence to the AURIX.
/// Each Ethernet packet contains one request; the final commit packet makes the
/// staged sequence active. The firmware then appends its built-in cyclic phase.
/// </summary>
public static class OsramStartupSequenceCommand
{
    private const ushort EtherType = 0x88B5;
    private const ushort MagicCommand = 0x434D;
    private const byte CmdSequenceStep = 0x08;
    private const byte CmdSequenceCommit = 0x09;
    private const int MaxRequestLength = 10;
    private const int KnownOsramStartupSteps = 1291;
    private const int CycleLength = 32;

    public static void Send(string pcapDeviceName, IReadOnlyList<LsmCanDiagRecord> trace, Action<string>? log = null)
    {
        var startup = ExtractStartup(trace, log);
        if (startup.Count == 0)
            throw new InvalidOperationException("The trace does not contain a complete OSRAM start-up sequence.");

        var existing = CaptureDeviceList.Instance
            .OfType<LibPcapLiveDevice>()
            .FirstOrDefault(d => string.Equals(d.Name, pcapDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"NIC not found: {pcapDeviceName}");

        if (startup.Count > ushort.MaxValue)
            throw new InvalidOperationException("The start-up sequence is too long for the Ethernet protocol.");

        var txDev = new LibPcapLiveDevice(existing.Interface);
        txDev.Open(DeviceModes.Promiscuous, read_timeout: 1);
        try
        {
            ushort count = (ushort)startup.Count;
            for (ushort index = 0; index < count; index++)
            {
                var step = startup[index];
                var packet = BuildPacket(CmdSequenceStep, 24);
                packet[17] = (byte)(index >> 8);
                packet[18] = (byte)index;
                packet[19] = (byte)(count >> 8);
                packet[20] = (byte)count;
                packet[21] = (byte)(step.GapUs >> 24);
                packet[22] = (byte)(step.GapUs >> 16);
                packet[23] = (byte)(step.GapUs >> 8);
                packet[24] = (byte)step.GapUs;
                packet[25] = step.Length;
                packet[26] = step.ExpectResponse;
                Array.Copy(step.Data, 0, packet, 27, MaxRequestLength);
                txDev.SendPacket(packet);
            }

            var commit = BuildPacket(CmdSequenceCommit, 8);
            commit[17] = (byte)(count >> 8);
            commit[18] = (byte)count;
            txDev.SendPacket(commit);
            log?.Invoke($"[cmd] Uploaded OSRAM startup sequence: {startup.Count} steps");
        }
        finally
        {
            txDev.Close();
        }
    }

    private static List<SequenceStep> ExtractStartup(IReadOnlyList<LsmCanDiagRecord> trace, Action<string>? log)
    {
        var records = new List<LsmCanDiagRecord>();

        foreach (var record in trace.OrderBy(r => r.Sequence))
        {
            if (record.DeviceId != 1 || record.IsCanRawFrame || record.Status != LsmCanDiagStatus.Ok)
                continue;

            int length = record.Operation == LsmCanDiagOperation.Read ? 4 : record.RawLength;
            if (length <= 0 || length > MaxRequestLength || record.RawPayload.Length < length)
                continue;

            records.Add(record);
        }

        if (records.Count == 0)
            return [];

        if (!HasStartupSignature(records))
        {
            log?.Invoke("[trace] OSRAM startup signature not found; replay rejected");
            return [];
        }

        int startupCount = FindStartupBoundary(records);
        if (startupCount < KnownOsramStartupSteps)
        {
            log?.Invoke($"[trace] OSRAM startup is incomplete: boundary={startupCount}, expected at least {KnownOsramStartupSteps}");
            return [];
        }

        var result = new List<SequenceStep>(startupCount);
        for (int i = 0; i < startupCount; i++)
        {
            var record = records[i];
            int length = record.Operation == LsmCanDiagOperation.Read ? 4 : record.RawLength;

            var data = new byte[MaxRequestLength];
            Array.Copy(record.RawPayload, data, length);
            result.Add(new SequenceStep(record.InterFrameDelayUs, (byte)length,
                record.Operation == LsmCanDiagOperation.Read ? (byte)1 : (byte)0, data));
        }

        log?.Invoke($"[trace] OSRAM startup boundary: {startupCount} steps from {records.Count} valid records");
        return result;
    }

    private static int FindStartupBoundary(List<LsmCanDiagRecord> records)
    {
        // NormalRun is a repeating 32-step cycle. Find two equal adjacent cycles
        // and use their first position as the end of the one-shot startup trace.
        int bestCandidate = -1;
        int bestDistance = int.MaxValue;
        for (int start = 1; start + CycleLength * 2 <= records.Count; start++)
        {
            bool equal = true;
            for (int offset = 0; offset < CycleLength && equal; offset++)
            {
                if (!HasSameRequest(records[start + offset], records[start + CycleLength + offset]))
                    equal = false;
            }

            if (equal && start >= KnownOsramStartupSteps - 8)
            {
                int distance = Math.Abs(start - KnownOsramStartupSteps);
                if (distance < bestDistance)
                {
                    bestCandidate = start;
                    bestDistance = distance;
                }
            }
        }

        if (bestCandidate >= 0)
            return bestCandidate;

        return -1;
    }

    private static bool HasStartupSignature(List<LsmCanDiagRecord> records)
    {
        const string InitialPoll = "80A520010001E5CB";
        const string InitialConfigWrite = "80A5200060F5764A";
        const string InitialStatusRead = "80A5BE0060F5";

        int initialPolls = 0;
        int prefixLength = Math.Min(20, records.Count);
        for (int i = 0; i < prefixLength; i++)
        {
            if (records[i].Operation == LsmCanDiagOperation.Write &&
                HasRequest(records[i], InitialPoll))
                initialPolls++;
        }

        bool hasConfigWrite = records.Take(prefixLength)
            .Any(r => r.Operation == LsmCanDiagOperation.Write && HasRequest(r, InitialConfigWrite));
        bool hasStatusRead = records.Take(prefixLength)
            .Any(r => r.Operation == LsmCanDiagOperation.Read && HasRequest(r, InitialStatusRead));

        return initialPolls >= 5 && hasConfigWrite && hasStatusRead;
    }

    private static bool HasRequest(LsmCanDiagRecord record, string expectedHex)
    {
        int length = expectedHex.Length / 2;
        if (record.RawPayload.Length < length)
            return false;

        for (int i = 0; i < length; i++)
        {
            if (record.RawPayload[i] != Convert.ToByte(expectedHex.Substring(i * 2, 2), 16))
                return false;
        }

        return true;
    }

    private static bool HasSameRequest(LsmCanDiagRecord left, LsmCanDiagRecord right)
    {
        if (left.Operation != right.Operation || left.RawLength != right.RawLength)
            return false;

        int length = left.Operation == LsmCanDiagOperation.Read ? 4 : left.RawLength;
        return left.RawPayload.AsSpan(0, length).SequenceEqual(right.RawPayload.AsSpan(0, length));
    }

    private static byte[] BuildPacket(byte command, int payloadLength)
    {
        var packet = new byte[Math.Max(60, 14 + payloadLength)];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        packet[6] = 0x02; packet[7] = 0x0A; packet[8] = 0xF0;
        packet[9] = 0x4E; packet[10] = 0x49; packet[11] = 0x02;
        packet[12] = (byte)(EtherType >> 8); packet[13] = (byte)(EtherType & 0xFF);
        packet[14] = (byte)(MagicCommand >> 8); packet[15] = (byte)(MagicCommand & 0xFF);
        packet[16] = command;
        return packet;
    }

    private sealed record SequenceStep(uint GapUs, byte Length, byte ExpectResponse, byte[] Data);
}