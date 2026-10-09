namespace Deguffer.Core.Duplicates;

/// <summary>
/// The checksum a content search groups by (§7.4). Naming it lets a user compare a value with one
/// another tool printed. A checksum groups and never licenses a removal, which reads the bytes again.
/// </summary>
public enum ChecksumAlgorithm
{
    /// <summary>XXH128, the default: the fastest, and wide enough that a collision is not the risk.</summary>
    XxHash128,

    Sha256,

    Sha512,

    Sha1,

    Md5,

    Crc32,

    /// <summary>Offered only where Windows provides it (Windows 11 build 25324 and later).</summary>
    Sha3_256,
}
