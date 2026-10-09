using System.IO.Hashing;
using System.Security.Cryptography;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// A file's checksum by one algorithm, as other tools print it, so a user can compare the value with
/// one they printed elsewhere (§7.4).
///
/// <para>Held as its lowercase hexadecimal text, which is both its display form and a value that
/// compares by content: a digest held as a byte array would compare by reference, and two equal
/// checksums would never group.</para>
/// </summary>
public readonly record struct ContentChecksum
{
    internal ContentChecksum(ChecksumAlgorithm algorithm, ReadOnlySpan<byte> digest)
    {
        Algorithm = algorithm;
        Hex = Convert.ToHexStringLower(digest);
    }

    public ChecksumAlgorithm Algorithm { get; }

    /// <summary>The digest in lowercase hexadecimal, in the byte order the algorithm is conventionally printed in.</summary>
    public string Hex { get; }

    public override string ToString() => Hex;
}

/// <summary>
/// One running checksum: bytes are appended in order, and <see cref="Finish"/> gives the value and
/// starts again, so a reader keeps one instance for every file it reads (G5).
///
/// <para>Two families stand behind it, which share no base type: XXH128 and CRC-32 from
/// <c>System.IO.Hashing</c>, and the cryptographic hashes from Windows (CNG) through
/// <see cref="IncrementalHash"/>.</para>
/// </summary>
internal abstract class Checksum : IDisposable
{
    private protected Checksum(ChecksumAlgorithm algorithm) => Algorithm = algorithm;

    public ChecksumAlgorithm Algorithm { get; }

    /// <summary>A running checksum by <paramref name="algorithm"/>, which must be one this machine offers.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The algorithm is not one <see cref="ChecksumAlgorithms.Offered"/> holds.</exception>
    public static Checksum For(ChecksumAlgorithm algorithm)
    {
        if (!ChecksumAlgorithms.IsOffered(algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "This machine cannot compute that checksum.");
        }

        return algorithm switch
        {
            ChecksumAlgorithm.XxHash128 => new NonCryptographic(algorithm, new XxHash128(), littleEndian: false),

            // System.IO.Hashing writes a CRC-32 least significant byte first, and the tools that print
            // one, 7-Zip and the CRC catalogue's check values among them, print it most significant
            // first: "123456789" is cbf43926, never 2639f4cb.
            ChecksumAlgorithm.Crc32 => new NonCryptographic(algorithm, new Crc32(), littleEndian: true),
            ChecksumAlgorithm.Sha256 => new Cryptographic(algorithm, HashAlgorithmName.SHA256),
            ChecksumAlgorithm.Sha512 => new Cryptographic(algorithm, HashAlgorithmName.SHA512),
            ChecksumAlgorithm.Sha1 => new Cryptographic(algorithm, HashAlgorithmName.SHA1),
            ChecksumAlgorithm.Md5 => new Cryptographic(algorithm, HashAlgorithmName.MD5),
            ChecksumAlgorithm.Sha3_256 => new Cryptographic(algorithm, HashAlgorithmName.SHA3_256),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "No checksum has that name."),
        };
    }

    public abstract void Append(ReadOnlySpan<byte> data);

    /// <summary>The checksum of everything appended since the last call, after which it starts again.</summary>
    public abstract ContentChecksum Finish();

    public virtual void Dispose() => GC.SuppressFinalize(this);

    private sealed class NonCryptographic(ChecksumAlgorithm algorithm, NonCryptographicHashAlgorithm hash, bool littleEndian)
        : Checksum(algorithm)
    {
        public override void Append(ReadOnlySpan<byte> data) => hash.Append(data);

        public override ContentChecksum Finish()
        {
            Span<byte> digest = stackalloc byte[hash.HashLengthInBytes];
            hash.GetHashAndReset(digest);

            if (littleEndian)
            {
                digest.Reverse();
            }

            return new ContentChecksum(Algorithm, digest);
        }
    }

    private sealed class Cryptographic(ChecksumAlgorithm algorithm, HashAlgorithmName name) : Checksum(algorithm)
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(name);

        public override void Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);

        public override ContentChecksum Finish()
        {
            Span<byte> digest = stackalloc byte[_hash.HashLengthInBytes];
            _hash.GetHashAndReset(digest);

            return new ContentChecksum(Algorithm, digest);
        }

        public override void Dispose()
        {
            _hash.Dispose();
            base.Dispose();
        }
    }
}
