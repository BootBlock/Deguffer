using System.Collections.Concurrent;
using System.IO.Enumeration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// One entry of a folder a temporary-folder row examines: what a <see cref="TempMarker"/> recognises
/// it by, and nothing else.
/// </summary>
/// <param name="Name">The entry's own name.</param>
/// <param name="Path">The entry's path as the plan names it, without the long-path prefix.</param>
/// <param name="Attributes">Whether it is a directory, and whether it is a link, are read from these.</param>
/// <param name="LastWriteTimeUtc">When it was last written, for a file the plan offers on its age.</param>
public readonly record struct TempFolderEntry(
    string Name,
    string Path,
    FileAttributes Attributes,
    DateTime LastWriteTimeUtc);

/// <summary>
/// One listing of each folder the temporary-folder rows examine, shared by every one of them for a
/// planning pass.
///
/// <para><b>Why shared.</b> Three <see cref="TempMarkerProviderBase"/> rows start from this account's
/// temporary folders themselves, and a developer's temporary folder holds tens of thousands of
/// entries that build tools, installers and test runners left there. Unshared, each of the three lists
/// it in full. Shared, the first row to ask lists it and the rest read that listing, so the rows also
/// decide on one view of the folder rather than on readings taken moments apart. Each row keeps its
/// own markers, owners and rules: only the listing is common.</para>
///
/// <para><b>All or nothing, as <see cref="FolderEntries"/> is.</b> A folder that is not there holds
/// nothing, and a folder that refused to be listed, or failed partway, is null rather than the part
/// that was read, so every row that reads it says it could not be read. The refusal is remembered as
/// a listing is, so a folder that refuses is asked once per pass rather than once per row.</para>
///
/// <para><b>Kept for a pass.</b> <see cref="Invalidate"/> drops every listing, which each row asks for
/// before a pass plans, so a rescan sees the folder as it is then. The listings are also dropped when
/// the last <see cref="Hold"/> of the pass ends, because nothing reads them afterwards and a temporary
/// folder's listing is megabytes. Explore's reading is not a pass's and does not come here: see
/// <see cref="TempMarkerProviderBase.DiscoverToolRootsAsync"/>.</para>
///
/// <para>The "Temporary files" row lists the same folders again for <see cref="TemporaryUserWork"/>,
/// directories only, through <see cref="ChildDirectories"/>. It is another row with its own reading,
/// and does not read here.</para>
/// </summary>
public sealed class TempFolderListings
{
    private readonly ConcurrentDictionary<string, PassMemo<Reading>> _read = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, IReadOnlyList<TempFolderEntry>?> _list;
    private readonly Lock _gate = new();
    private int _holds;

    public TempFolderListings()
        : this(Read)
    {
    }

    /// <param name="list">Lists one folder: the seam a test counts the listings through.</param>
    internal TempFolderListings(Func<string, IReadOnlyList<TempFolderEntry>?> list) => _list = list;

    /// <summary>
    /// The entries directly inside <paramref name="folder"/>, listed fresh: empty where it is not
    /// there, and null where it refused to be listed or failed partway.
    /// </summary>
    public static IReadOnlyList<TempFolderEntry>? Read(string folder) =>
        FolderEntries.Of(
            folder,
            static (ref FileSystemEntry entry) => new TempFolderEntry(
                entry.FileName.ToString(),
                LongPath.Display(entry.ToFullPath()),
                entry.Attributes,
                entry.LastWriteTimeUtc.UtcDateTime));

    /// <summary>
    /// The entries directly inside <paramref name="folder"/>, listed once until the pass ends, on the
    /// terms of <see cref="Read"/>. Safe to ask from every row at once: the first lists, and the rest
    /// wait for its answer.
    /// </summary>
    public IReadOnlyList<TempFolderEntry>? Of(string folder) =>
        _read.GetOrAdd(Path.TrimEndingDirectorySeparator(folder), static _ => new())
            .Get(() => new Reading(_list(folder)))
            .Entries;

    /// <summary>Drop every listing, so the next pass lists each folder as it is then.</summary>
    public void Invalidate() => _read.Clear();

    /// <summary>
    /// Keep every listing until the hold ends, and drop them all when the last hold still standing ends.
    /// Counted rather than paired, as <see cref="Scanning.Mft.MftVolumeIndexCache.Hold"/> is, because
    /// every row of a pass takes one and the first row to finish must not take the listing from the rest.
    /// </summary>
    public PassHold Hold()
    {
        lock (_gate)
        {
            _holds++;
        }

        return new PassHold(Release);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_holds == 0)
            {
                Invalidate();
            }
        }
    }

    /// <summary>A listing or a refusal, boxed so <see cref="PassMemo{T}"/> remembers a refusal too.</summary>
    private sealed record Reading(IReadOnlyList<TempFolderEntry>? Entries);
}
