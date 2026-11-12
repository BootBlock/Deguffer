using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What a row's own listings saw while it planned, so §5.6's record of a path the row protects is
/// taken from the listing rather than asked of the disk a second time (G5). See
/// <see cref="ProtectedPathCapture"/>.
///
/// <para><b>A listing only ever proves presence.</b> An entry it returned was there, and a folder it
/// listed was there. A path it did not return is not recorded as absent, because the listing may
/// have spelled it differently, and a protected path wrongly recorded as absent is one §5.6 stops
/// checking. Such a path is probed, and a refusal stays <see cref="PathPresence.Refused"/>.</para>
///
/// <para><b>Whether a directory held content is recorded only where the listing settled it</b>, by
/// the rule <see cref="DirectoryContent.IsPresent"/> answers: a file that is not a link holds nothing,
/// a folder listed whole holds content where it held a file or a link, and nothing where it held no
/// entry at all. Anything else, a folder holding only folders or a link, is asked.</para>
///
/// <para>Written while the row lists and read once it protects, never both at once.</para>
/// </summary>
internal sealed class ListedEntries
{
    /// <summary>Each path seen, and whether it held content where the listing settled that.</summary>
    private readonly Dictionary<string, bool?> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An entry a listing returned.</summary>
    public void Add(FileSystemInfo entry) =>
        Record(entry.FullName, entry is FileInfo && !IsLink(entry) ? false : null);

    /// <summary>Every entry a listing returned.</summary>
    public void AddAll(IEnumerable<FileSystemInfo> entries)
    {
        foreach (var entry in entries)
        {
            Add(entry);
        }
    }

    /// <summary>
    /// A folder listed whole, and every entry the listing returned. Only a complete listing may be
    /// given: a folder that refused to be listed was not seen.
    /// </summary>
    public void AddFolder(string folder, IReadOnlyList<FileSystemInfo> entries)
    {
        Record(
            folder,
            entries.Count == 0 ? false
            : entries.Any(entry => entry is FileInfo || IsLink(entry)) ? true
            : null);

        AddAll(entries);
    }

    /// <summary>
    /// Everything <paramref name="other"/> saw. Where both saw a path, a settled answer about its
    /// content is kept over an unsettled one.
    /// </summary>
    public void Include(ListedEntries? other)
    {
        foreach (var (path, heldContent) in other?._seen ?? [])
        {
            Record(path, heldContent);
        }
    }

    /// <summary>Whether a listing saw <paramref name="path"/>, and what it settled about its content.</summary>
    /// <param name="heldContent">Null where the listing did not settle it, so it must still be asked.</param>
    public bool TryGet(string path, out bool? heldContent) => _seen.TryGetValue(Key(path), out heldContent);

    private void Record(string path, bool? heldContent)
    {
        var key = Key(path);

        if (heldContent is not null || !_seen.ContainsKey(key))
        {
            _seen[key] = heldContent;
        }
    }

    /// <summary>One spelling per location: the display form, without a trailing separator.</summary>
    private static string Key(string path) => Path.TrimEndingDirectorySeparator(LongPath.Display(path));

    private static bool IsLink(FileSystemInfo entry) => (entry.Attributes & FileAttributes.ReparsePoint) != 0;
}
