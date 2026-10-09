using System.Security.Cryptography;

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

/// <summary>Which <see cref="ChecksumAlgorithm"/>s this machine can compute.</summary>
public static class ChecksumAlgorithms
{
    /// <summary>
    /// Every algorithm a search may be given here, in the order the page lists them. SHA3-256 is
    /// among them only where Windows' cryptographic providers have it, because .NET asks CNG for it
    /// rather than carrying its own.
    /// </summary>
    public static IReadOnlyList<ChecksumAlgorithm> Offered { get; } =
        [.. Enum.GetValues<ChecksumAlgorithm>().Where(algorithm => algorithm is not ChecksumAlgorithm.Sha3_256 || SHA3_256.IsSupported)];

    public static bool IsOffered(ChecksumAlgorithm algorithm) => Offered.Contains(algorithm);

    /// <summary>
    /// How many bytes the algorithm's value is, or null for a number no algorithm has, so a stored
    /// value can be told to be the right size for the algorithm stored beside it.
    /// </summary>
    internal static int? DigestBytes(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Crc32 => 4,
        ChecksumAlgorithm.XxHash128 or ChecksumAlgorithm.Md5 => 16,
        ChecksumAlgorithm.Sha1 => 20,
        ChecksumAlgorithm.Sha256 or ChecksumAlgorithm.Sha3_256 => 32,
        ChecksumAlgorithm.Sha512 => 64,
        _ => null,
    };

    /// <summary>The name other tools print the algorithm under, so a value can be compared with theirs.</summary>
    public static string Name(this ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.XxHash128 => "XXH128",
        ChecksumAlgorithm.Sha256 => "SHA-256",
        ChecksumAlgorithm.Sha512 => "SHA-512",
        ChecksumAlgorithm.Sha1 => "SHA-1",
        ChecksumAlgorithm.Md5 => "MD5",
        ChecksumAlgorithm.Crc32 => "CRC-32",
        ChecksumAlgorithm.Sha3_256 => "SHA3-256",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "No checksum has that name."),
    };
}
