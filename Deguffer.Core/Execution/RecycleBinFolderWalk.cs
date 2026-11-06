using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// Why Windows' Recycle Bin cannot take a folder whole for what is inside it: a path too long for the
/// bin, a folder Windows will not list, or files that together are longer than the bin may hold. See
/// <see cref="RecycleBinReach"/> for what was measured.
///
/// <para><b>On the walk every scan takes, and stopped at the first reason.</b> A confirmation waits
/// on this, and a removal asks it again of each item as it moves it. The folder is listed by several
/// workers at once through <see cref="IFileSystem.ListEntries"/>, and the walk ends as soon as one
/// reason is found, so a folder larger than its bin is not read to its last file.</para>
///
/// <para><b>Which reason is given where two apply can differ between two asks.</b> The workers
/// meet the folders in no fixed order, so a folder holding both a path too long and more than the bin
/// may hold is refused for whichever the walk met first. It is refused either way. Within one listing,
/// every path is judged before any length is counted, so a listing that both holds a path too long
/// and takes the total past the limit gives the path.</para>
///
/// <para><b>No path is built to be measured.</b> Each folder is handed down the length its path
/// has in display form, the form the shell is handed, and an entry's is that length and its name's.
/// A path is built only for the entry a refusal names.</para>
/// </summary>
internal sealed class RecycleBinFolderWalk
{
    // The display length of the folder's path and the separator after it, where a name inside begins.
    private readonly int _inside;
    private readonly long _limit;
    private long _total;
    private string? _reason;

    private RecycleBinFolderWalk(string shown, long limit)
    {
        _inside = shown.Length + (Path.EndsInDirectorySeparator(shown) ? 0 : 1);
        _limit = limit;
    }

    /// <summary>
    /// Why the bin cannot take the folder at <paramref name="extended"/> whole, or null where nothing
    /// inside it stops it. A link inside is moved as a link, and nothing on its far side goes with it,
    /// so it is not looked through.
    /// </summary>
    /// <param name="extended">The folder, in the extended form §6.3 requires. Not itself a link.</param>
    /// <param name="limit">The most the bin of the folder's volume may hold, in bytes.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static string? WhyNot(string extended, long limit, IFileSystem fs, WalkTuning tuning, CancellationToken ct)
    {
        var walk = new RecycleBinFolderWalk(LongPath.Display(extended), limit);

        // The walk's own token, so a reason found by one worker stops the rest.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            BoundedFileWalk.Visit(
                extended,
                walk._inside,
                tuning,
                FileNames.All,
                fs.ListEntries,
                (prefix, contents, descend) =>
                {
                    if (walk.Read(prefix, contents, descend))
                    {
                        stop.Cancel();
                    }
                },
                static _ => { },
                TimeProvider.System,
                stop.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && Volatile.Read(ref walk._reason) is not null)
        {
            // Stopped here, at the first reason, rather than by the caller.
        }

        return walk._reason;
    }

    /// <summary>
    /// Judge one folder's listing, and descend into the folders it holds. True where it found the
    /// reason the bin cannot take the folder, which ends the walk.
    /// </summary>
    /// <param name="prefix">The display length of the folder's path and the separator after it.</param>
    private bool Read(int prefix, DirectoryContents contents, Action<WalkEntry, int> descend)
    {
        // A folder gone since its parent was listed counts too, as it did when the listing threw: what
        // is there by the time the shell moves the folder is not known.
        if (contents.WasRefused)
        {
            return Refuse(
                $"Windows would not list '{LongPath.Display(contents.Directory)}', so Deguffer cannot tell whether "
                + $"the Recycle Bin can take the folder whole, and {RecycleBinReach.DeletesWhatItCannotTake}");
        }

        if ((TooLong(prefix, contents.Entries) ?? TooLong(prefix, contents.Links) ?? TooLong(prefix, contents.ReparseFiles))
            is { } entry)
        {
            var shown = LongPath.Display(entry.FullName);

            return Refuse(
                $"It holds '{shown[_inside..]}', whose path is {shown.Length:N0} characters long. "
                + $"{RecycleBinReach.TakesNoLongerPath}, and deletes the whole folder outright instead.");
        }

        long length = 0;

        foreach (var child in contents.Entries)
        {
            if (child.IsDirectory)
            {
                descend(child, prefix + child.Name.Length + 1);
            }
            else
            {
                length += child.Length;
            }
        }

        // A file carrying a reparse point is moved as it is, and its length is what the listing read.
        foreach (var child in contents.ReparseFiles)
        {
            length += child.Length;
        }

        var total = Interlocked.Add(ref _total, length);

        return total > _limit
            && Refuse(
                $"It holds at least {FreeSpace.Format(total)}, more than this drive's Recycle Bin can hold "
                + $"({FreeSpace.Format(_limit)}), and Windows deletes outright what is larger than its bin.");
    }

    private static WalkEntry? TooLong(int prefix, IReadOnlyList<WalkEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (prefix + entry.Name.Length > RecycleBinReach.LongestPath)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Keep <paramref name="reason"/> unless another worker found one first. Always true.</summary>
    private bool Refuse(string reason)
    {
        Interlocked.CompareExchange(ref _reason, reason, null);
        return true;
    }
}
