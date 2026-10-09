using System.Buffers;
using Deguffer.Core.Safety;
using Microsoft.Win32.SafeHandles;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// Compares a copy to remove with the copy its group keeps, byte for byte, immediately before the
/// removal (§7.4), through the handles both are held by.
///
/// <para><b>A checksum groups; it never licenses a removal.</b> The real false matches in the
/// established tools came from a cache fault and a sampled checksum, and only reading both files
/// catches every one, so the group's checksum is never consulted here.</para>
///
/// <para><b>A file is more than its main stream.</b> Each file's named streams are listed through
/// the handle held on it, never by its path, and must be the same streams holding the same bytes,
/// apart from <c>Zone.Identifier</c>, which records only where a download came from. A copy whose
/// streams hold data the kept copy lacks is not removed.</para>
/// </summary>
internal static class CopyComparison
{
    /// <summary>The stream Windows writes to say where a download came from, which holds nothing of the file.</summary>
    private const string ZoneIdentifier = ":Zone.Identifier:$DATA";

    private const int ChunkBytes = 1024 * 1024;

    /// <summary>
    /// Why <paramref name="copy"/> does not hold what <paramref name="kept"/> holds, with a sentence,
    /// or null where both hold the same bytes and the same named streams.
    ///
    /// <para><b>The lengths first.</b> A group matched on its name or time alone can hold a copy that
    /// is only the start of the copy kept, and a comparison of the shorter length would find them
    /// equal. Each length is the one the search found, which each held handle was shown to still
    /// have.</para>
    ///
    /// <para><b>A read that fails leaves the copy where it is.</b> Another program can hold part of
    /// either file locked while sharing it for reading, and the read then fails. That is this copy's
    /// answer, never the run's: a run that has begun reports what it did and verifies it.</para>
    /// </summary>
    /// <param name="copyStreams">The copy's named streams as they were compared, for the caller to find unchanged before the removal.</param>
    public static (RemovalCheck Check, string Why)? Compare(
        SafeFileHandle kept,
        long keptLength,
        SafeFileHandle copy,
        long copyLength,
        out IReadOnlyList<NamedStream> copyStreams,
        CancellationToken ct)
    {
        copyStreams = [];

        if (keptLength != copyLength)
        {
            return (RemovalCheck.ContentDiffers, "It is not the length of the copy kept, so the two do not hold the same bytes.");
        }

        try
        {
            return CompareContent(kept, copy, copyLength, out copyStreams, ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (RemovalCheck.Unreadable,
                "Windows would not let Deguffer read all of it or of the copy kept: another program may hold part of one locked.");
        }
    }

    private static (RemovalCheck Check, string Why)? CompareContent(
        SafeFileHandle kept, SafeFileHandle copy, long length, out IReadOnlyList<NamedStream> copyStreams, CancellationToken ct)
    {
        copyStreams = [];

        switch (Same(kept, copy, length, ct))
        {
            case null:
                return (RemovalCheck.Changed, "It or the copy kept ended before the length the search found, so it changed after the search.");

            case false:
                return (RemovalCheck.ContentDiffers, "Its bytes differ from the copy kept, so it is not a duplicate of it, whatever the checksum said.");
        }

        if (StreamsOf(kept) is not { } keptStreams || StreamsOf(copy) is not { } streams)
        {
            return (RemovalCheck.Unreadable, "Windows would not list its named streams or the copy kept's, so Deguffer could not show they hold the same.");
        }

        copyStreams = streams;

        if (streams.FirstOrDefault(stream => !Holds(keptStreams, stream)) is { Name: not null } extra)
        {
            return (RemovalCheck.StreamsDiffer, $"It holds a named stream, '{Display(extra.Name)}', that the copy kept lacks or holds at another length, so removing it would lose that data.");
        }

        if (keptStreams.FirstOrDefault(stream => !Holds(streams, stream)) is { Name: not null } missing)
        {
            return (RemovalCheck.StreamsDiffer, $"The copy kept holds a named stream, '{Display(missing.Name)}', that this copy lacks, so the two are not the same file.");
        }

        foreach (var stream in streams)
        {
            using var keptStream = NamedStreams.Open(kept, stream.Name, shareDelete: false);
            using var copyStream = NamedStreams.Open(copy, stream.Name, shareDelete: true);

            if (keptStream is null || copyStream is null)
            {
                return (RemovalCheck.Unreadable, $"Windows would not open its named stream '{Display(stream.Name)}' or the copy kept's, so Deguffer could not compare them.");
            }

            if (Same(keptStream, copyStream, stream.Length, ct) is not true)
            {
                return (RemovalCheck.StreamsDiffer, $"Its named stream '{Display(stream.Name)}' holds other data than the copy kept's, so removing it would lose that data.");
            }
        }

        return null;
    }

    /// <summary>
    /// The named streams of the file <paramref name="handle"/> is open on that a comparison weighs,
    /// <c>Zone.Identifier</c> left out, or null where Windows would not list them.
    /// </summary>
    public static IReadOnlyList<NamedStream>? StreamsOf(SafeFileHandle handle) =>
        FileInformation.StreamsOf(handle) is { } streams
            ? [.. streams.Where(stream => !stream.Name.Equals(ZoneIdentifier, StringComparison.OrdinalIgnoreCase))]
            : null;

    /// <summary>Whether two listings name the same streams at the same lengths. NTFS names streams without regard to case.</summary>
    public static bool SameStreams(IReadOnlyList<NamedStream> left, IReadOnlyList<NamedStream> right) =>
        left.Count == right.Count && left.All(stream => Holds(right, stream));

    private static bool Holds(IReadOnlyList<NamedStream> streams, NamedStream stream) =>
        streams.Any(other => other.Length == stream.Length && other.Name.Equals(stream.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether two handles hold the same <paramref name="length"/> bytes, or null where either ended
    /// first. Read in chunks, asking between them whether the run was stopped.
    /// </summary>
    private static bool? Same(SafeFileHandle left, SafeFileHandle right, long length, CancellationToken ct)
    {
        var leftBuffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        var rightBuffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);

        try
        {
            for (long offset = 0; offset < length;)
            {
                ct.ThrowIfCancellationRequested();

                var count = (int)Math.Min(ChunkBytes, length - offset);

                if (!Fill(left, leftBuffer.AsSpan(0, count), offset) || !Fill(right, rightBuffer.AsSpan(0, count), offset))
                {
                    return null;
                }

                if (!leftBuffer.AsSpan(0, count).SequenceEqual(rightBuffer.AsSpan(0, count)))
                {
                    return false;
                }

                offset += count;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer);
            ArrayPool<byte>.Shared.Return(rightBuffer);
        }
    }

    /// <summary>Fills <paramref name="buffer"/> from <paramref name="offset"/>, or answers false where the file ended first.</summary>
    private static bool Fill(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (buffer.Length > 0)
        {
            var read = RandomAccess.Read(handle, buffer, offset);

            if (read == 0)
            {
                return false;
            }

            buffer = buffer[read..];
            offset += read;
        }

        return true;
    }

    /// <summary>A stream's own name, as a reader knows it, from the <c>:name:$DATA</c> Windows lists.</summary>
    private static string Display(string stream) => stream.Split(':', StringSplitOptions.RemoveEmptyEntries)[0];
}
