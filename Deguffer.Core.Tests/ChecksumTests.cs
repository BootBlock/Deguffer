using System.Security.Cryptography;
using System.Text;
using Deguffer.Core.Duplicates;

namespace Deguffer.Core.Tests;

/// <summary>
/// Each checksum a search offers agrees with a value published for it, printed as other tools print
/// it, so a user comparing one with another tool's output compares like with like (§7.4).
/// </summary>
public sealed class ChecksumTests
{
    /// <summary>
    /// The published vectors, each from its algorithm's own reference: FIPS 180-2's "abc" examples for
    /// SHA-1, SHA-256 and SHA-512, RFC 1321's for MD5, the CRC catalogue's check value for CRC-32
    /// (the CRC of "123456789"), and xxHash's own value for the empty input, which is what
    /// <c>xxh128sum</c> prints for an empty file.
    /// </summary>
    [Theory]
    [InlineData(ChecksumAlgorithm.Sha1, "abc", "a9993e364706816aba3e25717850c26c9cd0d89d")]
    [InlineData(ChecksumAlgorithm.Sha256, "abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData(
        ChecksumAlgorithm.Sha512,
        "abc",
        "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f")]
    [InlineData(ChecksumAlgorithm.Md5, "abc", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData(ChecksumAlgorithm.Crc32, "123456789", "cbf43926")]
    [InlineData(ChecksumAlgorithm.XxHash128, "", "99aa06d3014798d86001c324468d497f")]
    public void EachChecksumAgreesWithItsPublishedValue(ChecksumAlgorithm algorithm, string input, string expected)
    {
        Assert.Equal(expected, Of(algorithm, Encoding.ASCII.GetBytes(input)).Hex);
    }

    /// <summary>
    /// XXH128 of "a", from the python-xxhash test suite (<c>tests/test_xxh3_128.py</c>), which gives
    /// it as the 128-bit integer. <c>xxh128sum</c> prints that integer's most significant half first,
    /// so its hexadecimal form is the printed one. The empty input above cannot tell the two halves
    /// apart in the wrong order, so a value that is not empty is needed too.
    /// </summary>
    [Fact]
    public void XxHash128IsPrintedMostSignificantHalfFirst()
    {
        var published = UInt128.Parse("225219434562328483135862406050043285023");

        Assert.Equal(published.ToString("x32"), Of(ChecksumAlgorithm.XxHash128, "a"u8.ToArray()).Hex);
    }

    /// <summary>
    /// FIPS 202's SHA3-256 of "abc" where Windows provides SHA3-256, and a refusal where it does not,
    /// rather than a value from somewhere else. xUnit 2 cannot skip at run time, so the branch this
    /// machine takes is the one asserted: Windows 11 build 25324 and later take the first.
    /// </summary>
    [Fact]
    public void Sha3_256AgreesWithItsPublishedValueOrIsRefusedWhereWindowsLacksIt()
    {
        if (!SHA3_256.IsSupported)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Checksum.For(ChecksumAlgorithm.Sha3_256));
            return;
        }

        Assert.Equal(
            "3a985da74fe225b2045c172d6bd390bd855f086e3e9d525b46bfe24511431532",
            Of(ChecksumAlgorithm.Sha3_256, "abc"u8.ToArray()).Hex);
    }

    [Fact]
    public void Sha3_256IsOfferedExactlyWhereWindowsHasIt()
    {
        Assert.Equal(SHA3_256.IsSupported, ChecksumAlgorithms.IsOffered(ChecksumAlgorithm.Sha3_256));
        Assert.True(ChecksumAlgorithms.IsOffered(ChecksumAlgorithm.XxHash128));
    }

    /// <summary>
    /// A reader appends a file a block at a time, so a checksum that started again on each append
    /// would give every file the value of its last block. Each piece here is a different length, so
    /// none lands on a boundary the algorithm keeps internally.
    /// </summary>
    [Theory]
    [MemberData(nameof(Offered))]
    public void AppendingInPiecesGivesTheValueOfAppendingOnce(ChecksumAlgorithm algorithm)
    {
        var data = new byte[1000];
        new Random(297).NextBytes(data);
        using var pieces = Checksum.For(algorithm);

        pieces.Append(data.AsSpan(0, 1));
        pieces.Append(data.AsSpan(1, 300));
        pieces.Append(data.AsSpan(301, 699));

        Assert.Equal(Of(algorithm, data), pieces.Finish());
    }

    /// <summary>One instance serves every file a reader reads, so finishing one must leave nothing for the next.</summary>
    [Theory]
    [MemberData(nameof(Offered))]
    public void FinishingStartsAgain(ChecksumAlgorithm algorithm)
    {
        using var checksum = Checksum.For(algorithm);

        checksum.Append("first file"u8);
        checksum.Finish();
        checksum.Append("abc"u8);

        Assert.Equal(Of(algorithm, "abc"u8.ToArray()), checksum.Finish());
    }

    /// <summary>Two checksums group only where they compare by value, as two digests held in two arrays would not.</summary>
    [Fact]
    public void TwoEqualChecksumsAreEqualAndOfTheirAlgorithm()
    {
        var one = Of(ChecksumAlgorithm.Sha256, "abc"u8.ToArray());
        var other = Of(ChecksumAlgorithm.Sha256, "abc"u8.ToArray());

        Assert.Equal(one, other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());
        Assert.Equal(ChecksumAlgorithm.Sha256, one.Algorithm);
    }

    public static TheoryData<ChecksumAlgorithm> Offered() => [.. ChecksumAlgorithms.Offered];

    private static ContentChecksum Of(ChecksumAlgorithm algorithm, byte[] data)
    {
        using var checksum = Checksum.For(algorithm);
        checksum.Append(data);

        return checksum.Finish();
    }
}
