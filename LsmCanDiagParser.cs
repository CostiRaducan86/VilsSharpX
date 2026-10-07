using System;
using System.Buffers.Binary;
using System.Diagnostics;

namespace VilsSharpX;

public static class LsmCanDiagParser
{
    private static readonly long TimestampOriginTicks = DateTime.UtcNow.Ticks;
    private static readonly long TimestampOriginStopwatch = Stopwatch.GetTimestamp();

    /// <summary>Clock used for <see cref="LsmCanDiagRecord.ReceivedUtc"/>; compare record times only against it.</summary>
    public static DateTime CaptureUtcNow => GetCaptureUtcNow();

    private static DateTime GetCaptureUtcNow()
    {
        long elapsedStopwatch = Stopwatch.GetTimestamp() - TimestampOriginStopwatch;
        long elapsedDateTimeTicks = (long)(elapsedStopwatch * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);
        return new DateTime(TimestampOriginTicks + elapsedDateTimeTicks, DateTimeKind.Utc);
    }

    public static bool TryParseEthernet(ReadOnlySpan<byte> frame, out LsmCanDiagRecord? record)
    {
        record = null;

        if (frame.Length < 14)
            return false;

        int offset = 12;
        ushort etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset, 2));
        offset += 2;

        while (etherType == 0x8100 || etherType == 0x88A8)
        {
            if (frame.Length < offset + 4)
                return false;

            offset += 2;
            etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset, 2));
            offset += 2;
        }

        if (etherType != LsmCanDiagRecord.EtherType)
            return false;

        if (frame.Length < offset + LsmCanDiagRecord.HeaderLength + LsmCanDiagRecord.PayloadFixed)
            return false;

        var header = frame.Slice(offset, LsmCanDiagRecord.HeaderLength);
        ushort magic = BinaryPrimitives.ReadUInt16BigEndian(header[..2]);
        if (magic != LsmCanDiagRecord.Magic)
            return false;

        byte version = header[2];
        byte recordType = header[3];
        ushort sequence = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4, 2));
        ushort payloadLength = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(6, 2));

        // Accept v1 (24 bytes) and v2 (94 bytes = 22 fixed + 72 raw UART)
        bool isV1 = version == 1 && payloadLength == 24;
        bool isV2 = version == 2 && payloadLength == LsmCanDiagRecord.PayloadLength;
        if (!isV1 && !isV2)
            return false;

        if (recordType != LsmCanDiagRecord.RegisterIoRecordType)
            return false;

        if (frame.Length < offset + LsmCanDiagRecord.HeaderLength + payloadLength)
            return false;

        var payload = frame.Slice(offset + LsmCanDiagRecord.HeaderLength, payloadLength);
        byte statusRaw = payload[20];
        byte rawLen = payload[21];

        byte[] rawPayload;
        if (isV2 && rawLen > 0 && payload.Length >= LsmCanDiagRecord.PayloadFixed + rawLen)
        {
            rawPayload = payload.Slice(LsmCanDiagRecord.PayloadFixed, rawLen).ToArray();
        }
        else
        {
            // v1 or no raw: synthesise from value field
            uint v32 = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(10, 4));
            rawPayload =
            [
                (byte)(v32 >> 24), (byte)(v32 >> 16), (byte)(v32 >> 8), (byte)v32
            ];
            if (rawLen == 0) rawLen = 4;
        }

        record = new LsmCanDiagRecord
        {
            Sequence = sequence,
            RecordType = recordType,
            SourceTimestamp = BinaryPrimitives.ReadUInt32BigEndian(payload[..4]),
            Address = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2)),
            ResponseDelayUs = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6, 2)),
            InterFrameDelayUs = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(8, 2)),
            Value = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(10, 4)),
            Checksum = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(14, 4)),
            DeviceId = payload[18],
            Operation = payload[19] switch
            {
                (byte)LsmCanDiagOperation.Write  => LsmCanDiagOperation.Write,
                (byte)LsmCanDiagOperation.CanRaw => LsmCanDiagOperation.CanRaw,
                _                                => LsmCanDiagOperation.Read,
            },
            Status = Enum.IsDefined(typeof(LsmCanDiagStatus), statusRaw)
                ? (LsmCanDiagStatus)statusRaw
                : LsmCanDiagStatus.Unsupported,
            RawLength = rawLen,
            RawPayload = rawPayload,
            ReceivedUtc = GetCaptureUtcNow(),
        };

        return true;
    }
}
