using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>How a listing ended.</summary>
/// <param name="Denied">
/// Whether a refusal was the account not being allowed to read the directory, which no retry changes.
/// </param>
internal readonly record struct ListingEnd(PathPresence Presence, bool Denied);

/// <summary>
/// List <paramref name="directory"/> into <paramref name="into"/> with <paramref name="options"/>,
/// returning how the listing ended. Entries listed before a failure stay in the list.
/// </summary>
internal delegate ListingEnd ListDirectory(string directory, EnumerationOptions options, List<WalkEntry> into);

/// <summary>
/// One worker's listing of one directory at a time, sorted into what <see cref="DirectoryContents"/>
/// hands a caller.
///
/// <para>Two rules live here and nowhere else. §5.3: a directory that cannot be read is skipped rather
/// than raised, because a locked or refused path is the operating system protecting live state rather
/// than an error. It is reported as refused, so a caller can qualify its total, and never as an
/// exception. And a reparse point is kept apart from the ordinary children: a junction's target holds
/// its own place on the volume, so counting through one both double-counts and describes a tree the
/// caller never classified.</para>
///
/// <para>The lists are reused for every directory the worker reads, because a walk reads hundreds of
/// thousands of them and the lists are what a listing would otherwise allocate (G4).</para>
/// </summary>
internal sealed class WalkListing(ListingBuffer buffer, ListDirectory list)
{
    private readonly List<WalkEntry> _entries = [];
    private readonly List<WalkEntry> _links = [];
    private readonly List<WalkEntry> _reparseFiles = [];

    public WalkListing(ListingBuffer buffer)
        : this(buffer, FromDisk)
    {
    }

    /// <param name="directory">In the extended form (§6.3).</param>
    public DirectoryContents Read(string directory)
    {
        _entries.Clear();
        _links.Clear();
        _reparseFiles.Clear();

        var options = buffer.Options;
        var end = list(directory, options, _entries);

        if (buffer.RetryAfter(options, end) is { } smaller)
        {
            // Started again rather than continued: a listing cannot be resumed part-way, and keeping
            // what the first attempt read would hand those entries back twice.
            _entries.Clear();

            var retried = list(directory, smaller, _entries);
            if (retried.Presence is PathPresence.Present)
            {
                buffer.Rejected();
            }

            end = retried;
        }

        SetReparsePointsApart();

        // Expected on a live machine, and reported rather than thrown, since a volume holds hundreds.
        // A directory gone since its parent was listed counts as refused too: its bytes were never
        // read, so the totals above it are lower bounds either way.
        return new DirectoryContents(_entries, _links, _reparseFiles, WasRefused: end.Presence is not PathPresence.Present);
    }

    private void SetReparsePointsApart()
    {
        var marked = 0;

        foreach (var entry in _entries)
        {
            if (entry.IsReparsePoint)
            {
                (entry.IsDirectory ? _links : _reparseFiles).Add(entry);
                marked++;
            }
        }

        // Most directories hold none, and those pay for the one pass above and nothing else.
        if (marked > 0)
        {
            _entries.RemoveAll(static entry => entry.IsReparsePoint);
        }
    }

    private static ListingEnd FromDisk(string directory, EnumerationOptions options, List<WalkEntry> into)
    {
        using var listing = new DirectoryListing<WalkEntry>(
            directory,
            (ref System.IO.Enumeration.FileSystemEntry entry) => WalkEntry.Of(directory, ref entry),
            options: options);

        while (listing.MoveNext())
        {
            into.Add(listing.Current);
        }

        return new ListingEnd(listing.Outcome, listing.WasDenied);
    }
}
