using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// §5.5's fallback: measure a tree by walking it, with a bounded worker pool.
///
/// This is the approach the audit measured at over ten minutes on a handful of profile subtrees,
/// so it is explicitly *not* the scanner — it is what runs where the MFT cannot be read, and every
/// result it produces carries the reason (see <see cref="FallbackReason"/>).
///
/// It reports file lengths, and the entries a removal would take, and nothing else.
/// <c>FileInfo.Length</c> cannot see how many clusters a
/// compressed or sparse file occupies, and neither can any cheap call: <c>GetCompressedFileSize</c>
/// returns the length again for anything not compressed, and the call that does answer needs a
/// handle per file — measured at 156 times the cost of the length pass over a 426 MB cache. So the
/// lengths are the measurement, they are exact, and <see cref="ScanSize.Reclaimable"/> is the
/// number they feed. Nothing here is a guess, which is why <see cref="ScanSize.FromLengths"/> does
/// not mark its results approximate.
/// </summary>
public sealed class ParallelEnumerationScanner : IDirectoryScanner
{
    /// <summary>
    /// One instance per reason, built once. G5: this type is stateless apart from the reason it
    /// stamps on results, and <see cref="Because"/> sits on the fallback path — which §6.3 makes
    /// the *ordinary* path — so constructing one per measurement would allocate per directory
    /// scanned for no benefit.
    /// </summary>
    private static readonly ParallelEnumerationScanner[] ByReason =
        [.. Enum.GetValues<FallbackReason>().Order().Select(r => new ParallelEnumerationScanner(r))];

    public static readonly ParallelEnumerationScanner Default = ByReason[(int)FallbackReason.None];

    private readonly FallbackReason _reason;

    private ParallelEnumerationScanner(FallbackReason reason) => _reason = reason;

    /// <summary>Same walk, attributed to whichever reason sent the caller here.</summary>
    public ParallelEnumerationScanner Because(FallbackReason reason) => ByReason[(int)reason];

    /// <summary>
    /// A file is sized in one read and reported as <see cref="ScanStrategy.DirectRead"/>, dropping
    /// the reason this instance was built with.
    ///
    /// Dropping it is the honest answer rather than a shortcut: a fallback reason explains why a
    /// walk was necessary, and no walk happened. Carrying it would put "scanned by walking
    /// directories, which is slower" on every plan naming a single file, and — where the reason was
    /// <see cref="FallbackReason.NotElevated"/> — offer administrator rights that would change the
    /// measurement not at all.
    ///
    /// This is also the one place a file is measured at all. <see cref="DirectoryScanner"/> reaches
    /// it the ordinary way, because its index cannot resolve a file path and so hands every one of
    /// them here.
    /// </summary>
    public ValueTask<ScanResult> MeasureAsync(
        string path,
        MinimumAge keep = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default) =>
        new(Task.Run(
            () => TryMeasureFile(path, keep) is { } file
                ? ScanResult.Direct(file.Size, file.WithheldRecent) with { MailStores = file.MailStores }
                : Slow(Measure(path, keep, progress, ct)),
            ct));

    /// <summary>
    /// Nothing is remembered here, so every reading is already taken from the disk.
    ///
    /// The guard is deliberately absent. This member exists for the executor's before-and-after
    /// around a tool's own eviction command, and §5.1 leaves that command the authority on what it
    /// removes — a total that excluded recent files would then be subtracted from one the command
    /// did not respect.
    /// </summary>
    public ValueTask<ScanResult> MeasureFromDiskAsync(string path, CancellationToken ct = default) =>
        MeasureAsync(path, MinimumAge.Off, progress: null, ct);

    /// <summary>
    /// Always null: this scanner holds no index, so it has nothing to search. Answering by walking
    /// here would hide the walk behind the accelerator's signature, and §5.5 requires the slow
    /// route to be visible to the caller that takes it.
    /// </summary>
    public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesNamedAsync(
        string name,
        string root,
        CancellationToken ct = default) => new((IReadOnlyList<string>?)null);

    /// <summary>Nothing is retained between calls, so there is nothing to drop.</summary>
    public void Invalidate()
    {
    }

    private ScanResult Slow((ScanSize Size, bool WithheldRecent, IReadOnlyList<string> MailStores) measured) =>
        ScanResult.Slow(measured.Size, _reason, measured.WithheldRecent) with { MailStores = measured.MailStores };

    private static (ScanSize Size, bool WithheldRecent, IReadOnlyList<string> MailStores) Measure(
        string path,
        MinimumAge keep,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        if (!LongPath.DirectoryExists(path))
        {
            return (ScanSize.Zero, false, []);
        }

        long total = 0;
        long entries = 0;

        // An int rather than a bool because the walk sets it from several threads at once, and
        // Interlocked has no bool overload. Only ever moved to 1, so no ordering question arises.
        var withheld = 0;

        var folders = new ConcurrentBag<VisitedFolder>();
        var stores = new ConcurrentBag<string>();

        BoundedFileWalk.Visit<VisitedFolder?>(
            LongPath.Extended(path),
            rootState: null,
            (holder, contents, descend) =>
            {
                var folder = new VisitedFolder(holder);
                folders.Add(folder);

                // A folder the walk was refused may hold anything, so the removal cannot empty it.
                if (contents.WasRefused)
                {
                    folder.Stays();
                }

                foreach (var entry in contents.Entries)
                {
                    if (entry is DirectoryInfo child)
                    {
                        descend(child, folder);
                    }

                    // §9, and before the guard for the reason the removal asks it first: the store is
                    // left whatever its age, so it is out of the total and its folders stay.
                    else if (entry is FileInfo store && MailStore.Is(store.Name))
                    {
                        stores.Add(LongPath.Display(store.FullName));
                        folder.Stays();
                    }

                    // The walk hands over a FileInfo whose attributes and timestamps were populated by
                    // the enumeration that found it, so asking its age here costs no further I/O (G4).
                    // A file the guard keeps contributes nothing, because the removal will not take it.
                    else if (entry is FileInfo file && keep.Protects(file))
                    {
                        Interlocked.Exchange(ref withheld, 1);
                        folder.Stays();
                    }
                    else if (entry is FileInfo counted)
                    {
                        Interlocked.Add(ref total, counted.Length);
                        Interlocked.Increment(ref entries);
                    }
                }

                // A link is removed as a link: one entry, and nothing on its far side. The guard
                // applies to it exactly as the removal applies it, by the link's own timestamp.
                foreach (var link in contents.Links)
                {
                    if (keep.Protects(link))
                    {
                        Interlocked.Exchange(ref withheld, 1);
                        folder.Stays();
                    }
                    else
                    {
                        Interlocked.Increment(ref entries);
                    }
                }

                // A file carrying a reparse point is counted by nothing here, as the walk never has
                // counted one. One named like a store is still named and keeps its folders, because the
                // removal leaves it whatever its mark means: a OneDrive placeholder and a deduplicated
                // file carry the mark too.
                foreach (var marked in contents.ReparseFiles)
                {
                    if (MailStore.Is(marked.Name))
                    {
                        stores.Add(LongPath.Display(marked.FullName));
                        folder.Stays();
                    }
                }
            },
            // §5.5: stream partial results. One report per breadth-first level, not per file.
            () => progress?.Report(ScanSize.FromLengths(Interlocked.Read(ref total))),
            ct);

        var removed = Interlocked.Read(ref entries) + folders.Count(folder => !folder.HoldsSomethingThatStays);

        return (
            new ScanSize(Interlocked.Read(ref total), Interlocked.Read(ref total), Entries: removed),
            Volatile.Read(ref withheld) == 1,
            [.. stores.Order(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// One folder the walk reached, and whether the removal would leave it standing.
    ///
    /// <para>A folder stays while anything inside it stays, however deep, so a kept file has to mark
    /// every folder above it. The walk is breadth-first and parallel, so no folder's children are all
    /// known when it is reached; each folder instead holds the one above it, and a kept entry walks the
    /// chain upward.</para>
    /// </summary>
    private sealed class VisitedFolder(VisitedFolder? holder)
    {
        private readonly VisitedFolder? _holder = holder;

        private int _stays;

        public bool HoldsSomethingThatStays => Volatile.Read(ref _stays) == 1;

        /// <summary>
        /// Mark this folder and every folder above it. It stops at a folder already marked, because
        /// whatever marked that one goes on to mark everything above it.
        /// </summary>
        public void Stays()
        {
            for (var folder = this; folder is not null && Interlocked.Exchange(ref folder._stays, 1) == 0; folder = folder._holder)
            {
            }
        }
    }

    /// <summary>
    /// The length of <paramref name="path"/> if it is a file, or null for anything else — a
    /// directory, an absent path, or one we were refused. §5.3 makes the refusal ordinary rather
    /// than an error, and a null then leaves the caller to measure the path as a directory, which
    /// answers zero for something that is not there.
    ///
    /// Deguffer measured only directories until <c>C:\Windows\MEMORY.DMP</c>, which is a single
    /// file and the largest reclaim it knows about. Answering zero for it — which is what this
    /// scanner did — produces a step nobody can select, because a step with nothing to reclaim is
    /// not offerable.
    /// </summary>
    private static (ScanSize Size, bool WithheldRecent, IReadOnlyList<string> MailStores)? TryMeasureFile(
        string path,
        MinimumAge keep)
    {
        try
        {
            var file = new FileInfo(LongPath.Extended(path));

            if (!file.Exists)
            {
                return null;
            }

            // §9: zero, and named, whatever its age and whatever mark it carries, because the removal
            // leaves it either way. The same answer a kept file gets, for the same reason, with the
            // store's own name on it so the plan can protect it.
            if (MailStore.Is(file.Name))
            {
                return (ScanSize.Zero, false, [LongPath.Display(file.FullName)]);
            }

            // A link's length is its own, not its target's, and following one would count a tree
            // this scanner never looked inside — the same rule BoundedFileWalk applies to every
            // entry it enumerates.
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            // A file the guard keeps is still a file, so this stays an answer rather than becoming
            // a null that would send the caller off to measure it as a directory. Zero is what the
            // removal will reclaim from it, and a step with nothing to reclaim is not offerable —
            // which is the outcome a protected single file should have.
            return keep.Protects(file)
                ? (ScanSize.Zero, true, [])
                : (new ScanSize(file.Length, file.Length, Entries: 1), false, []);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
