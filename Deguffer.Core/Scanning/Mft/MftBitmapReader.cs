namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// Reads <c>$MFT</c>'s <c>$BITMAP</c> from where <see cref="IMftSource.Bitmap"/> says it is.
/// </summary>
internal static class MftBitmapReader
{
    /// <summary>
    /// The bits of records <c>0</c> to <paramref name="count"/>, or null where the source does not
    /// know where the bitmap is or it cannot be read. Either way a pass reads every record, so null
    /// costs time and never a file.
    /// </summary>
    public static MftBitmap? TryRead(IMftSource source, long count)
    {
        if (source.Bitmap is not { } placement)
        {
            return null;
        }

        // Only the bytes covering the records the pass reads. The bitmap can run past the table, and
        // its length comes off the disk, so it is never what a buffer is sized from.
        var wanted = (int)Math.Min(placement.Length, (count + 7) / 8);

        if (placement.Value is { } value)
        {
            return new MftBitmap(value.AsSpan(0, Math.Min(wanted, value.Length)).ToArray());
        }

        return MftClusterValue.TryRead(source.TryReadClusters, source.BytesPerCluster, placement.Runs, wanted) is { } bits
            ? new MftBitmap(bits)
            : null;
    }
}
