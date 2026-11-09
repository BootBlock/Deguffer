using System.Buffers.Binary;
using System.Text;

namespace Deguffer.Testing;

/// <summary>An ATA attribute to put in a synthesised table.</summary>
public readonly record struct AtaEntry(byte Id, byte Value, ulong Raw = 0, bool PreFail = false, byte Worst = 100);

/// <summary>
/// The structures a drive answers with, built from their documented layouts, so a test states a drive
/// the machine does not have. The serial numbers are invented.
/// </summary>
public static class DriveBytes
{
    public const byte Usb = 0x07;
    public const byte Sata = 0x0B;
    public const byte Nvme = 0x11;

    public const string InventedSerial = "TESTSERIAL0001";

    private const int SectorBytes = 512;
    private const int ProtocolHeaderBytes = 48;

    public static byte[] Device(byte bus, string product, string? serial = InventedSerial, string revision = "1.0", string vendor = "", bool removable = false)
    {
        var bytes = new byte[512];

        bytes[10] = removable ? (byte)1 : (byte)0;
        bytes[28] = bus;

        var next = 64;
        Text(12, vendor);
        Text(16, product);
        Text(20, revision);
        Text(24, serial);

        return bytes;

        void Text(int field, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(field), next);
            next += Encoding.ASCII.GetBytes(value, bytes.AsSpan(next)) + 1;
        }
    }

    public static byte[] SeekPenalty(bool incurs)
    {
        var bytes = new byte[12];
        bytes[8] = incurs ? (byte)1 : (byte)0;
        return bytes;
    }

    public static byte[] Trim(bool enabled)
    {
        var bytes = new byte[12];
        bytes[8] = enabled ? (byte)1 : (byte)0;
        return bytes;
    }

    public static byte[] Alignment(int logical, int physical)
    {
        var bytes = new byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), logical);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), physical);
        return bytes;
    }

    public static byte[] Geometry(long size)
    {
        var bytes = new byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), size);
        return bytes;
    }

    public static byte[] DeviceNumber(uint number)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), number);
        return bytes;
    }

    /// <summary>An NVMe health log with every counter 0 but those named, at 37 °C unless told otherwise.</summary>
    public static byte[] NvmeHealth(
        byte criticalWarning = 0,
        int kelvin = 310,
        byte spare = 100,
        byte spareThreshold = 10,
        byte percentageUsed = 0,
        ulong powerOnHours = 0,
        ulong mediaErrors = 0,
        int[]? sensors = null)
    {
        var bytes = Protocol(512);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(ProtocolHeaderBytes + 1), (ushort)kelvin);
        bytes[ProtocolHeaderBytes + 3] = spare;
        bytes[ProtocolHeaderBytes + 4] = spareThreshold;
        bytes[ProtocolHeaderBytes + 5] = percentageUsed;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(ProtocolHeaderBytes + 128), powerOnHours);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(ProtocolHeaderBytes + 160), mediaErrors);

        foreach (var (kelvinValue, index) in (sensors ?? []).Select((value, index) => (value, index)))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(ProtocolHeaderBytes + 200 + (index * 2)), (ushort)kelvinValue);
        }
        bytes[ProtocolHeaderBytes] = criticalWarning;
        return bytes;
    }

    public static byte[] NvmeController(int major = 1, int minor = 4, int warningKelvin = 0)
    {
        var bytes = Protocol(4096);

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(ProtocolHeaderBytes + 80), (uint)((major << 16) | (minor << 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(ProtocolHeaderBytes + 266), (ushort)warningKelvin);
        return bytes;
    }

    /// <summary>An NVMe self-test log whose newest entry has <paramref name="result"/> in its low nibble.</summary>
    public static byte[] NvmeSelfTest(byte result, ulong hours = 0)
    {
        var bytes = Protocol(564);

        bytes[ProtocolHeaderBytes + 4] = result;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(ProtocolHeaderBytes + 4 + 4), hours);
        return bytes;
    }

    /// <summary>The answer to <c>IOCTL_STORAGE_PREDICT_FAILURE</c>.</summary>
    public static byte[] Prediction(bool predictsFailure, params AtaEntry[] entries)
    {
        var bytes = new byte[4 + SectorBytes];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes, predictsFailure ? 1u : 0u);
        Table(bytes.AsSpan(4), entries);
        return bytes;
    }

    /// <summary>A thresholds sector, for <see cref="Ata"/> to wrap.</summary>
    public static byte[] Thresholds(params (byte Id, byte Threshold)[] entries)
    {
        var sector = new byte[SectorBytes];

        for (var i = 0; i < entries.Length; i++)
        {
            sector[2 + (i * 12)] = entries[i].Id;
            sector[3 + (i * 12)] = entries[i].Threshold;
        }

        return sector;
    }

    /// <summary>A sector as a <c>SMART_RCV_DRIVE_DATA</c> answer, with the driver's status.</summary>
    public static byte[] Ata(byte[] sector, byte driverError = 0)
    {
        var bytes = new byte[16 + SectorBytes];

        bytes[4] = driverError;
        sector.CopyTo(bytes, 16);
        return bytes;
    }

    public static byte[] Identify(int rotation, int capabilitiesGenerations = 0, int currentGeneration = 0)
    {
        var sector = new byte[SectorBytes];

        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(76 * 2), (ushort)capabilitiesGenerations);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(77 * 2), (ushort)(currentGeneration << 1));
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(217 * 2), (ushort)rotation);
        return sector;
    }

    /// <summary>An ATA self-test log whose entry at the 1-based <paramref name="index"/> is the latest.</summary>
    public static byte[] AtaSelfTest(int index, byte statusNibble, ushort hours)
    {
        var sector = new byte[SectorBytes];
        var entry = 2 + ((index - 1) * 24);

        sector[entry + 1] = (byte)(statusNibble << 4);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(entry + 2), hours);
        sector[508] = (byte)index;
        return sector;
    }

    public static byte[] AtaErrorLog(ushort count)
    {
        var sector = new byte[SectorBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(452), count);
        return sector;
    }

    private static void Table(Span<byte> table, AtaEntry[] entries)
    {
        for (var i = 0; i < entries.Length; i++)
        {
            var at = table[(2 + (i * 12))..];
            at[0] = entries[i].Id;
            at[1] = entries[i].PreFail ? (byte)1 : (byte)0;
            at[3] = entries[i].Value;
            at[4] = entries[i].Worst;

            var raw = entries[i].Raw;

            for (var b = 0; b < 6; b++)
            {
                at[5 + b] = (byte)(raw >> (b * 8));
            }
        }
    }

    /// <summary>A protocol answer holding <paramref name="dataBytes"/> at the offset the driver would declare.</summary>
    private static byte[] Protocol(int dataBytes)
    {
        var bytes = new byte[ProtocolHeaderBytes + dataBytes];

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8 + 16), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8 + 20), dataBytes);
        return bytes;
    }
}
