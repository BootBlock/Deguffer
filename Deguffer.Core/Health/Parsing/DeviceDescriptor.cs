using System.Buffers.Binary;
using System.Text;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Health.Parsing;

/// <summary>What a <c>STORAGE_DEVICE_DESCRIPTOR</c> says about a drive.</summary>
internal readonly record struct DeviceDescriptor(
    StorageBusType Bus,
    bool IsRemovable,
    string Vendor,
    string Product,
    string Revision,
    DriveSerial Serial)
{
    private const int RemovableOffset = 10;
    private const int VendorOffset = 12;
    private const int ProductOffset = 16;
    private const int RevisionOffset = 20;
    private const int SerialOffset = 24;
    private const int BusOffset = 28;
    private const int MinimumBytes = BusOffset + sizeof(int);

    /// <summary>The name to show: a vendor where the drive gives one, then its model.</summary>
    public string Model => string.IsNullOrEmpty(Vendor) ? Product : $"{Vendor} {Product}";

    internal static bool TryRead(ReadOnlySpan<byte> bytes, out DeviceDescriptor descriptor)
    {
        descriptor = default;

        if (bytes.Length < MinimumBytes)
        {
            return false;
        }

        descriptor = new DeviceDescriptor(
            (StorageBusType)bytes[BusOffset],
            bytes[RemovableOffset] != 0,
            Text(bytes, VendorOffset),
            Text(bytes, ProductOffset),
            Text(bytes, RevisionOffset),
            new DriveSerial(Text(bytes, SerialOffset)));

        return true;
    }

    /// <summary>
    /// The string a field points at. An offset of 0 means the drive has none, and one past the end is
    /// a malformed answer, so both read as absent.
    /// </summary>
    private static string Text(ReadOnlySpan<byte> bytes, int field)
    {
        var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes[field..]);

        if (offset <= 0 || offset >= bytes.Length)
        {
            return string.Empty;
        }

        var text = bytes[offset..];
        var end = text.IndexOf((byte)0);

        return Encoding.ASCII.GetString(end < 0 ? text : text[..end]).Trim();
    }
}
