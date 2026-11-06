using System.IO.Enumeration;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One reading of a <c>.vs</c> folder, from which the lock-file list, each cache's evidence and each
/// target's age are all taken, so a preview lists each folder in it once.
///
/// <para><b>Read by name and level alone, never by the evidence.</b> The lock-file list is taken from
/// a reading at the preview and from a new one at the clean, and a cache that changed in between is
/// the likeliest to be in use: a <c>DevHub.exe</c> still writing the index can leave a file in it the
/// evidence does not allow. So the reading enters every folder at a cache's name and level, to the
/// bottom, and every other folder at the <c>.vs</c> and solution levels, whether or not a solution
/// beside the <c>.vs</c> folder still names it. That is every folder the examination looks inside
/// too, so neither lists anything again.</para>
///
/// <para>A link is listed as an entry and never entered, so what it points at is neither read nor
/// asked about.</para>
/// </summary>
/// <param name="list">Lists one folder: the seam a test counts the listings through.</param>
internal sealed class VisualStudioCacheReader(Func<string, IReadOnlyList<VisualStudioEntry>?> list)
{
    public static readonly VisualStudioCacheReader Default = new(List);

    /// <summary>
    /// The entries directly inside <paramref name="folder"/>: empty where it is not there, and null
    /// where it refused to be listed or failed partway.
    /// </summary>
    public static IReadOnlyList<VisualStudioEntry>? List(string folder) =>
        FolderEntries.Of(
            folder,
            static (ref FileSystemEntry entry) => new VisualStudioEntry(
                entry.FileName.ToString(),
                LongPath.Display(entry.ToFullPath()),
                entry.Attributes,
                MinimumAge.NewestFileTimeOf(entry.CreationTimeUtc.UtcDateTime, entry.LastWriteTimeUtc.UtcDateTime)));

    /// <summary>The <c>.vs</c> folder at <paramref name="vs"/>, read as it is now.</summary>
    public VisualStudioListing Read(string vs, CancellationToken ct = default) =>
        Level(vs, VisualStudioLevel.Folder, ct);

    private VisualStudioListing Level(string directory, VisualStudioLevel level, CancellationToken ct) =>
        Listing(directory, ct, entry =>
            VisualStudioCacheLayout.CacheNamed(level, entry.Name, entry.Kind) is not null
                ? Whole(entry.Path, ct)
                : entry.Kind == TargetKind.Directory && level != VisualStudioLevel.Version
                    ? Level(entry.Path, level + 1, ct)
                    : null);

    private VisualStudioListing Whole(string directory, CancellationToken ct) =>
        Listing(directory, ct, entry => entry.Kind == TargetKind.Directory ? Whole(entry.Path, ct) : null);

    /// <param name="enter">The listing of a folder among the entries, or null for one not entered.</param>
    private VisualStudioListing Listing(string directory, CancellationToken ct, Func<VisualStudioEntry, VisualStudioListing?> enter)
    {
        ct.ThrowIfCancellationRequested();

        var entries = list(directory);
        // By the name exactly as listed. In a case-sensitive folder FileContentIndex and
        // fileContentIndex are two folders, and a key ignoring case would judge one by the other's
        // contents.
        var inner = new Dictionary<string, VisualStudioListing>(StringComparer.Ordinal);

        foreach (var entry in entries ?? [])
        {
            if (entry.Kind == TargetKind.Directory && !entry.IsLink && enter(entry) is { } listing)
            {
                inner[entry.Name] = listing;
            }
        }

        return new VisualStudioListing(directory, entries, inner);
    }
}
