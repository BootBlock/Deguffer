using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// The layout of <see cref="ChecksumCache"/>'s store, which holds numbers and no text, so no path can
/// be in it.
///
/// <para>A header (<c>DGCK</c>, the format's version and how many values follow), then each value:
/// the volume's serial number, the file's number in two halves, its length, its last-modified and
/// change times in ticks, the algorithm, the part read, the day it was last used, and the value's
/// bytes, as many as the algorithm gives. Last, an XXH64 of everything before it, so a store damaged
/// anywhere is told from a whole one. All numbers are little-endian.</para>
///
/// <para><b>Anything unexpected reads as no store at all</b>, never as part of one: a value read
/// wrongly would group files that differ or part files that match.</para>
/// </summary>
internal static class ChecksumStoreFormat
{
    private const int Version = 1;

    /// <summary>The magic, the version and the count.</summary>
    private const int HeaderBytes = 12;

    /// <summary>A value less its checksum's bytes: six numbers of 8 bytes, the algorithm, the part and the day.</summary>
    private const int FixedEntryBytes = (6 * sizeof(long)) + 2 + sizeof(int);

    private const int TrailerBytes = sizeof(ulong);

    /// <summary>The longest checksum any algorithm gives: SHA-512's.</summary>
    private const int LongestDigest = 64;

    /// <summary>The most a store of <see cref="ChecksumCache.MostKept"/> values can take, past which a file is not this store.</summary>
    public const long LongestStore = HeaderBytes + ((long)ChecksumCache.MostKept * (FixedEntryBytes + LongestDigest)) + TrailerBytes;

    private static ReadOnlySpan<byte> Magic => "DGCK"u8;

    public static void Write(Stream stream, IReadOnlyCollection<KeyValuePair<ChecksumKey, RememberedChecksum>> entries)
    {
        var buffer = new ArrayBufferWriter<byte>(HeaderBytes + (entries.Count * (FixedEntryBytes + 16)) + TrailerBytes);

        buffer.Write(Magic);
        WriteInt(buffer, Version);
        WriteInt(buffer, entries.Count);

        Span<byte> digest = stackalloc byte[LongestDigest];

        foreach (var (key, remembered) in entries)
        {
            var span = buffer.GetSpan(FixedEntryBytes + LongestDigest);
            BinaryPrimitives.WriteUInt64LittleEndian(span, key.Identity.Volume);
            BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)key.Identity.File);
            BinaryPrimitives.WriteUInt64LittleEndian(span[16..], (ulong)(key.Identity.File >> 64));
            BinaryPrimitives.WriteInt64LittleEndian(span[24..], key.Length);
            BinaryPrimitives.WriteInt64LittleEndian(span[32..], key.ModifiedTicks);
            BinaryPrimitives.WriteInt64LittleEndian(span[40..], key.ChangedTicks);
            span[48] = (byte)key.Algorithm;
            span[49] = (byte)key.Part;
            BinaryPrimitives.WriteInt32LittleEndian(span[50..], remembered.LastUsedDay);

            Convert.FromHexString(remembered.Value.Hex, digest, out _, out var written);
            digest[..written].CopyTo(span[FixedEntryBytes..]);
            buffer.Advance(FixedEntryBytes + written);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.GetSpan(TrailerBytes), XxHash64.HashToUInt64(buffer.WrittenSpan));
        buffer.Advance(TrailerBytes);

        stream.Write(buffer.WrittenSpan);
    }

    /// <summary>Every value <paramref name="bytes"/> holds, or null where they are not a whole store of this format.</summary>
    public static Dictionary<ChecksumKey, RememberedChecksum>? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes + TrailerBytes)
        {
            return null;
        }

        var body = bytes[..^TrailerBytes];

        if (XxHash64.HashToUInt64(body) != BinaryPrimitives.ReadUInt64LittleEndian(bytes[^TrailerBytes..])
            || !body.StartsWith(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(body[4..]) != Version
            || BinaryPrimitives.ReadInt32LittleEndian(body[8..]) is < 0 or > ChecksumCache.MostKept)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(body[8..]);
        var read = new Dictionary<ChecksumKey, RememberedChecksum>(count);
        var at = HeaderBytes;

        for (var i = 0; i < count; i++)
        {
            if (body.Length - at < FixedEntryBytes)
            {
                return null;
            }

            var entry = body[at..];
            var algorithm = (ChecksumAlgorithm)entry[48];
            var part = (ContentPart)entry[49];
            var length = BinaryPrimitives.ReadInt64LittleEndian(entry[24..]);

            if (ChecksumAlgorithms.DigestBytes(algorithm) is not { } digestBytes
                || !Enum.IsDefined(part)
                || length <= 0
                || body.Length - at < FixedEntryBytes + digestBytes)
            {
                return null;
            }

            var key = new ChecksumKey(
                new FileIdentity(
                    BinaryPrimitives.ReadUInt64LittleEndian(entry),
                    new UInt128(BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]), BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]))),
                length,
                BinaryPrimitives.ReadInt64LittleEndian(entry[32..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[40..]),
                algorithm,
                part);
            var value = new ContentChecksum(algorithm, entry.Slice(FixedEntryBytes, digestBytes));

            if (!read.TryAdd(key, new RememberedChecksum(value, BinaryPrimitives.ReadInt32LittleEndian(entry[50..]))))
            {
                return null;
            }

            at += FixedEntryBytes + digestBytes;
        }

        return at == body.Length ? read : null;
    }

    private static void WriteInt(ArrayBufferWriter<byte> buffer, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer.GetSpan(sizeof(int)), value);
        buffer.Advance(sizeof(int));
    }
}
