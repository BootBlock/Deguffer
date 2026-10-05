namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Where <c>$MFT</c>'s <c>$BITMAP</c> is kept: inside a record of the table, which only a small
/// table's is, or in clusters of its own.
///
/// <para>A placement rather than the bits, so a source can be opened without reading them, and so
/// the clusters are read through <see cref="IMftSource.TryReadClusters"/>, where whatever wraps the
/// source sees them as part of the pass.</para>
/// </summary>
public sealed class MftBitmapPlacement
{
    private MftBitmapPlacement(byte[]? value, IReadOnlyList<DataRun> runs, long length)
    {
        Value = value;
        Runs = runs;
        Length = length;
    }

    /// <summary>The bitmap's bytes, where they were kept inside a record. Null where they are in clusters.</summary>
    public byte[]? Value { get; }

    /// <summary>The clusters holding the bitmap, in order. Empty where it was kept inside a record.</summary>
    public IReadOnlyList<DataRun> Runs { get; }

    /// <summary>How many of the bitmap's bytes NTFS has written, and so how many are worth reading.</summary>
    public long Length { get; }

    public static MftBitmapPlacement InRecord(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new MftBitmapPlacement(value, [], value.Length);
    }

    public static MftBitmapPlacement InClusters(IReadOnlyList<DataRun> runs, long length)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        return new MftBitmapPlacement(null, runs, length);
    }
}
