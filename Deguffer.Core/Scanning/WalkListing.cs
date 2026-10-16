using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>How a listing ended.</summary>
/// <param name="Denied">
/// Whether a refusal was the account not being allowed to read the directory, which no retry changes.
/// </param>
/// <param name="WhileReading">
/// Whether the listing failed after the directory was opened, while its entries were read: the only
/// failure a different buffer can change. False for a listing that did not fail.
/// </param>
public readonly record struct ListingEnd(PathPresence Presence, bool Denied, bool WhileReading)
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;

    /// <summary>A directory listed to the end.</summary>
    public static ListingEnd Listed => new(PathPresence.Present, Denied: false, WhileReading: false);

    /// <summary>
    /// How a listing that failed with the Win32 error <paramref name="error"/> ended. A directory that
    /// is not there is <see cref="PathPresence.Absent"/>, decided on the two errors .NET turns into a
    /// <see cref="DirectoryNotFoundException"/>. Every other failure, access denied included, is
    /// <see cref="PathPresence.Refused"/>, which is what catching <see cref="UnauthorizedAccessException"/>
    /// and <see cref="IOException"/> made of it.
    /// </summary>
    public static ListingEnd Failed(int error, bool whileReading) => new(
        error is ErrorFileNotFound or ErrorPathNotFound ? PathPresence.Absent : PathPresence.Refused,
        Denied: error == ErrorAccessDenied,
        whileReading);
}

/// <summary>
/// List <paramref name="directory"/> into <paramref name="into"/> as <paramref name="request"/> asks,
/// and return how the listing ended. Entries listed before a failure stay in the list.
/// </summary>
internal delegate ListingEnd ListDirectory(string directory, ListingRequest request, List<WalkEntry> into);

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
/// <para>The lists and the <see cref="ListingMemory"/> are reused for every directory the worker reads,
/// because a walk reads hundreds of thousands of them and they are what a listing would otherwise
/// allocate (G4). Dispose it once the worker has ended.</para>
/// </summary>
internal sealed class WalkListing(ListingBuffer buffer, ListDirectory list, FileNames files) : IDisposable
{
    private readonly ListingMemory _memory = new(buffer.Largest);
    private readonly List<WalkEntry> _entries = [];
    private readonly List<WalkEntry> _links = [];
    private readonly List<WalkEntry> _reparseFiles = [];

    /// <param name="directory">In the extended form (§6.3).</param>
    /// <param name="followsLink">See <see cref="ListingRequest.FollowsLink"/>.</param>
    public DirectoryContents Read(string directory, bool followsLink)
    {
        _entries.Clear();
        _links.Clear();
        _reparseFiles.Clear();

        var bytes = buffer.Bytes;
        var end = list(directory, new ListingRequest(_memory, bytes, followsLink, files), _entries);

        if (buffer.RetryAfter(bytes, end) is { } smaller)
        {
            // Started again rather than continued: a listing cannot be resumed part-way, and keeping
            // what the first attempt read would hand those entries back twice.
            _entries.Clear();

            var retried = list(directory, new ListingRequest(_memory, smaller, followsLink, files), _entries);
            if (retried.Presence is PathPresence.Present)
            {
                buffer.Rejected();
            }

            end = retried;
        }

        SetReparsePointsApart();

        // Expected on a live machine, and reported rather than thrown, since a volume holds hundreds.
        return new DirectoryContents(directory, _entries, _links, _reparseFiles, end.Presence);
    }

    public void Dispose() => _memory.Dispose();

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
}
