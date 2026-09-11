using System;
using System.Collections.Generic;
using System.Linq;
using SharpPcap;
using SharpPcap.LibPcap;

namespace VilsSharpX;

/// <summary>Uploads the Nichia Direct Control startup sequence to AURIX.</summary>
public static class NichiaStartupSequenceCommand
{
    private const ushort EtherType = 0x88B5;
    private const ushort MagicCommand = 0x434D;
    private const byte CmdSequenceStep = 0x0A;
    private const byte CmdSequenceCommit = 0x0B;
    private const byte CmdSequenceHardcoded = 0x0C;
    private const int MaxRequestLength = 72;
    private const int NichiaStartupBoundary = 296;
    private const int NichiaInitialWriteCount = 7;
    private const int NichiaEepromStartIndex = 13;
    private const uint NichiaEepromResponseWireUs = 2250;

    public static void Send(string pcapDeviceName, IReadOnlyList<LsmCanDiagRecord> trace, Action<string>? log = null)
    {
        var startup = ExtractStartup(trace, log);
        if (startup.Count == 0)
            throw new InvalidOperationException("The trace does not contain a complete NICHIA start-up sequence.");

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
                var packet = BuildPacket(CmdSequenceStep, 24 + MaxRequestLength);
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

            var commit = BuildPacket(CmdSequenceCommit, 5);
            commit[17] = (byte)(count >> 8);
            commit[18] = (byte)count;
            txDev.SendPacket(commit);
            log?.Invoke($"[cmd] Uploaded NICHIA startup sequence: {startup.Count} steps");
        }
        finally
        {
            txDev.Close();
        }
    }

    public static void StartHardcoded(string pcapDeviceName, Action<string>? log = null)
    {
        var existing = CaptureDeviceList.Instance
            .OfType<LibPcapLiveDevice>()
            .FirstOrDefault(d => string.Equals(d.Name, pcapDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"NIC not found: {pcapDeviceName}");

        var packet = BuildPacket(CmdSequenceHardcoded, 3);
        using var txDev = new LibPcapLiveDevice(existing.Interface);
        txDev.Open(DeviceModes.Promiscuous, read_timeout: 1);
        try
        {
            for (int i = 0; i < 3; i++)
                txDev.SendPacket(packet);
            log?.Invoke("[cmd] Started built-in NICHIA startup sequence on AURIX");
        }
        finally
        {
            txDev.Close();
        }
    }

    private static List<SequenceStep> ExtractStartup(IReadOnlyList<LsmCanDiagRecord> trace, Action<string>? log)
    {
        var records = trace.OrderBy(r => r.Sequence)
            .Where(r => r.DeviceId == 0 && !r.IsCanRawFrame && r.Status == LsmCanDiagStatus.Ok)
            .ToList();

        if (records.Count < NichiaStartupBoundary)
            return [];

        if (!HasStartupSignature(records, log))
            return [];

        var startupRecords = records.Take(NichiaStartupBoundary + 1).ToList();
        if (CountInitialZeroWrites(startupRecords) > NichiaInitialWriteCount)
        {
            startupRecords.RemoveAt(NichiaInitialWriteCount);
            log?.Invoke("[trace] NICHIA removed one duplicate initial W 0x0000 to match Saleae ECU startup");
        }

        if (startupRecords.Count < NichiaStartupBoundary)
            return [];

        var result = new List<SequenceStep>(NichiaStartupBoundary);
        for (int i = 0; i < NichiaStartupBoundary; i++)
        {
            var record = startupRecords[i];
            int length = NichiaRequestLength(record);
            if (length <= 0 || length > MaxRequestLength || record.RawPayload.Length < length)
                return [];

            var data = new byte[MaxRequestLength];
            Array.Copy(record.RawPayload, data, length);
            result.Add(new SequenceStep(GetGapUs(startupRecords, i), (byte)length,
                record.Operation == LsmCanDiagOperation.Read ? (byte)1 : (byte)0, data));
        }

        log?.Invoke($"[trace] NICHIA startup boundary: {result.Count} steps from {records.Count} valid records");
        return result;
    }

    private static bool HasStartupSignature(List<LsmCanDiagRecord> records, Action<string>? log)
    {
        const int InitialWriteCount = 7;
        ReadOnlySpan<byte> InitialWrite =
        [
            0x55, 0x11, 0x0C, 0x00, 0x00, 0x08, 0x19
        ];

        if (records.Count <= InitialWriteCount)
        {
            log?.Invoke("[trace] NICHIA startup signature not found; replay rejected");
            return false;
        }

        for (int index = 0; index < InitialWriteCount; index++)
        {
            var record = records[index];
            if (record.Operation != LsmCanDiagOperation.Write ||
                record.RawPayload.Length < InitialWrite.Length ||
                !record.RawPayload.AsSpan(0, InitialWrite.Length).SequenceEqual(InitialWrite))
            {
                log?.Invoke("[trace] NICHIA startup signature not found; replay rejected");
                return false;
            }
        }

        ReadOnlySpan<byte> EepromReadRequest = [0x55, 0xB1, 0x1C, 0x05];
        bool hasEepromRead = false;
        int searchEnd = Math.Min(records.Count, InitialWriteCount + 3);
        for (int index = InitialWriteCount; index < searchEnd; index++)
        {
            var record = records[index];
            if (record.Operation == LsmCanDiagOperation.Write &&
                record.RawPayload.Length >= EepromReadRequest.Length &&
                record.RawPayload.AsSpan(0, EepromReadRequest.Length).SequenceEqual(EepromReadRequest))
            {
                hasEepromRead = true;
                break;
            }
        }

        if (!hasEepromRead)
            log?.Invoke("[trace] NICHIA startup signature not found; replay rejected");

        return hasEepromRead;
    }

    private static int CountInitialZeroWrites(List<LsmCanDiagRecord> records)
    {
        int count = 0;
        foreach (var record in records)
        {
            if (record.Operation != LsmCanDiagOperation.Write ||
                record.RawPayload.Length < 7 ||
                !record.RawPayload.AsSpan(0, 7).SequenceEqual(
                    new byte[] { 0x55, 0x11, 0x0C, 0x00, 0x00, 0x08, 0x19 }))
                break;
            count++;
        }
        return count;
    }

    private static uint GetGapUs(List<LsmCanDiagRecord> records, int index)
    {
        ushort recordedGap = records[index].InterFrameDelayUs;
        uint gapUs;

        if (recordedGap != ushort.MaxValue || index == 0)
            gapUs = recordedGap;
        else
        {
            long timestampGapUs = (long)(records[index].ReceivedUtc - records[index - 1].ReceivedUtc)
                .TotalMilliseconds * 1000L;
            gapUs = timestampGapUs > 0L ? (uint)Math.Min(timestampGapUs, uint.MaxValue) : recordedGap;
        }

        /* The C# trace delay includes the 64-byte EEPROM response wire time.
         * Saleae measures the requested idle from the response end instead. */
        if (index >= NichiaEepromStartIndex && gapUs > NichiaEepromResponseWireUs)
            gapUs -= NichiaEepromResponseWireUs;

        return gapUs;
    }

    private static int NichiaRequestLength(LsmCanDiagRecord record)
    {
        if (record.Operation == LsmCanDiagOperation.Write)
            return record.RawLength;

        if (record.RawPayload.Length < 3 || record.RawPayload[0] != 0x55)
            return 0;

        byte fun = (byte)(record.RawPayload[2] & 0x07);
        return fun is 5 or 7 ? (fun == 7 ? 5 : 4) : 0;
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