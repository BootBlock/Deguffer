using System.Collections.Concurrent;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// Measures the bytes a tree holds that nothing outside it also holds: the sum over files whose
/// hard-link count is exactly one. That is the number an eviction of the tree's contents can
/// actually give back, and for a store that hard-links into every consumer — pnpm's, conda's — it
/// is the only defensible figure. Summing file lengths there counts each linked copy, and the
/// reported reclaim would be several times what the disk returns, which is §5.4's lesson on a
/// different subject.
///
/// <para>This walk, and not the MFT, is deliberately the only route. The file record does carry a
/// hard-link count (at 0x12, which <see cref="Mft.MftRecordHeader.Read"/> leaves unread), but the
/// table is only readable elevated, and a link-aware figure that existed only under elevation would
/// disagree with the unelevated one for the same tree. <c>GetFileInformationByHandleEx</c> answers
/// per file at either privilege level — observed on a real hard link, unelevated, before this was
/// built — so one route serves both and the number never depends on how the app was launched.
/// <see cref="ScanResult.Fallback"/> is therefore <see cref="FallbackReason.None"/>: elevating
/// would not change anything, and the offer would be false.</para>
///
/// <para>Each file is read by name, with no handle, where its file system answers that
/// (<see cref="FileInformation.Standard"/>): over a synthesised store of 100,000 files on NTFS, with
/// no real-time antivirus scanning, the read by name took about four fifths of the time a handle per
/// file took (<c>Deguffer.Benchmark links</c>). Both read whatever the listed name is when it is
/// read, a link as the link, and the walk lists no file numbers to compare it with, so a file
/// replaced since the listing is measured as the file of this tree it now is. The figure is a
/// prediction marked approximate either way.</para>
///
/// <para>A file with several links all inside the tree is not counted, though deleting the whole
/// tree would free it. Telling that case apart means enumerating every link's name per multi-linked
/// file, for stores whose layout never produces it — content-addressed files link outward, not to
/// each other — so the cheap reading under-reports in the rare case rather than over-reporting in
/// any, which is the direction §5.4 allows.</para>
/// </summary>
public sealed class HardLinkAwareScanner : IDirectoryScanner
{
    /// <summary>The walk at the shipped values, for a scanner built outside the app (G5).</summary>
    public static readonly HardLinkAwareScanner Default = new(ScanTuner.Shipped);

    private readonly ScanTuner _tuner;
    private readonly FileInformation _files;

    /// <param name="tuner">What each walk runs with, asked as the walk starts.</param>
    public HardLinkAwareScanner(ScanTuner tuner)
        : this(tuner, FileInformation.Default)
    {
    }

    /// <param name="tuner">What each walk runs with, asked as the walk starts.</param>
    /// <param name="files">
    /// How each file's link count and sizes are read, so a test or the benchmark can choose the
    /// route a file system would leave it.
    /// </param>
    internal HardLinkAwareScanner(ScanTuner tuner, FileInformation files)
    {
        _tuner = tuner;
        _files = files;
    }

    public ValueTask<ScanResult> MeasureAsync(
        string path,
        MinimumAge keep = default,
        ScanBounds bounds = default,
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default) =>
        new(Task.Run(() => MeasureNow(path, keep, bounds, progress, ct), ct));

    /// <summary>
    /// The directory is asked about first, and the file only where no directory is there, so each
    /// path costs one attribute read in the ordinary case and a refusal is met once. The read fails
    /// the same way whichever kind is asked for, so a refused file is answered here as a refused
    /// directory would be.
    /// </summary>
    private ScanResult MeasureNow(
        string path,
        MinimumAge keep,
        ScanBounds bounds,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        var presence = LongPath.ProbeDirectory(path);

        if (presence is PathPresence.Present)
        {
            var walked = Measure(path, keep, LeftOutEntries.Of(path, bounds), _tuner, _files, progress, ct);

            return ScanResult.ByChoice(walked.Size, walked.WithheldRecent) with
            {
                MailStores = walked.MailStores,
                Root = walked.Root,
            };
        }

        if (presence is PathPresence.Absent)
        {
            presence = LongPath.ProbeFile(path);
        }

        if (presence is PathPresence.Present)
        {
            var file = MeasureFile(path, keep, _files);

            return ScanResult.Direct(file.Size, file.WithheldRecent) with { MailStores = file.MailStores };
        }

        return ScanResult.ByChoice(Approximate(0, 0)) with
        {
            Root = presence is PathPresence.Refused ? RootReach.NotDescribed : RootReach.Reached,
        };
    }

    /// <summary>Always null — this scanner holds no index. See <see cref="ParallelEnumerationScanner"/>.</summary>
    public ValueTask<IReadOnlyList<string>?> TryFindDirectoriesAsync(
        DirectorySearch search,
        string root,
        CancellationToken ct = default) => new((IReadOnlyList<string>?)null);

    /// <summary>Nothing is remembered here, so every reading is already taken from the disk.</summary>
    public ValueTask<ScanResult> MeasureFromDiskAsync(string path, CancellationToken ct = default) =>
        MeasureAsync(path, MinimumAge.Off, progress: null, ct: ct);

    /// <summary>Nothing is retained between calls, so there is nothing to drop.</summary>
    public void Invalidate()
    {
    }

    /// <summary>
    /// The walk of a directory Windows describes. See <see cref="MeasureNow"/>.
    ///
    /// <para>An entry <paramref name="leftOut"/> names is passed over, neither read nor entered, as the
    /// other walk passes it. A kept root needs nothing here, because this figure counts no entries.</para>
    /// </summary>
    private static (ScanSize Size, bool WithheldRecent, IReadOnlyList<string> MailStores, RootReach Root) Measure(
        string path,
        MinimumAge keep,
        LeftOutEntries? leftOut,
        ScanTuner tuning,
        FileInformation files,
        IProgress<ScanSize>? progress,
        CancellationToken ct)
    {
        long allocated = 0;
        long logical = 0;

        // See ParallelEnumerationScanner for why this is an int: the walk sets it from several
        // threads, and Interlocked has no bool overload.
        var withheld = 0;

        var stores = new ConcurrentBag<string>();
        var route = new ByNameRoute();

        // Written by the root's own listing alone, before anything below it is queued.
        var listed = true;

        BoundedFileWalk.Visit(
            LongPath.Extended(path),
            rootState: true,
            tuning,
            (isRoot, contents, descend) =>
            {
                if (isRoot && contents.WasRefused)
                {
                    listed = false;
                }

                var left = leftOut?.NamesIn(contents);

                foreach (var entry in contents.Entries)
                {
                    if (left?.Contains(entry.Name) == true)
                    {
                        continue;
                    }

                    if (entry.IsDirectory)
                    {
                        descend(entry, false);
                    }
                    else
                    {
                        OnFile(entry);
                    }
                }

                foreach (var marked in contents.ReparseFiles)
                {
                    if (left?.Contains(marked.Name) != true)
                    {
                        OnMarked(marked);
                    }
                }
            },
            // §5.5: stream partial results, at the walk's interval rather than per file.
            () => progress?.Report(Approximate(
                Interlocked.Read(ref allocated), Interlocked.Read(ref logical))),
            TimeProvider.System,
            ct);

        if (!listed)
        {
            return (Approximate(0, 0), false, [], RootReach.NotListed);
        }

        return (
            Approximate(Interlocked.Read(ref allocated), Interlocked.Read(ref logical)),
            Volatile.Read(ref withheld) == 1,
            [.. stores.Order(StringComparer.OrdinalIgnoreCase)],
            RootReach.Reached);

        void OnFile(WalkEntry file)
        {
            // §9, named rather than only left out: a store inside a tool's own folder is what
            // withholds that tool's command, and the plan can only say which store by its path.
            if (MailStore.Is(file.Name))
            {
                stores.Add(LongPath.Display(file.FullName));
                return;
            }

            // Asked before the file is read, because the guard is the cheaper question and the
            // answer is the same either way: a file it keeps is one this store's eviction
            // will not take, so its sole-linked bytes are not reclaimable here.
            if (keep.Protects(file.NewestFileTime))
            {
                Interlocked.Exchange(ref withheld, 1);
                return;
            }

            if (files.Standard(file.FullName, route) is { NumberOfLinks: 1 } sole)
            {
                Interlocked.Add(ref allocated, sole.AllocationSize);
                Interlocked.Add(ref logical, sole.EndOfFile);
            }
        }

        // A file carrying a reparse point holds none of this tree's sole-linked bytes, and one named
        // like a store is still named: the removal does not read which kind of mark a file carries.
        void OnMarked(WalkEntry marked)
        {
            if (MailStore.Is(marked.Name))
            {
                stores.Add(LongPath.Display(marked.FullName));
            }
        }
    }

    /// <summary>
    /// Both numbers here are read exactly, and the result is still marked approximate, because the
    /// claim it feeds is a prediction: link counts move whenever a project installs or removes
    /// dependencies, and the tool's eviction chooses by its own reference records rather than by
    /// counting links. A figure the next hour can change must not present itself as precise.
    /// </summary>
    /// <remarks>
    /// Counts no entries. What this predicts is what a tool's own eviction frees, and that tool
    /// decides what it removes, so there is no removal of Deguffer's here whose entries could be
    /// counted.
    /// </remarks>
    private static ScanSize Approximate(long allocated, long logical) =>
        new(allocated, logical, IsApproximate: true);

    /// <summary>
    /// The sole-link size of the file at <paramref name="path"/>, mirroring
    /// <see cref="ParallelEnumerationScanner"/>: a single named file is a legitimate subject, and
    /// answering zero for one would make its step unofferable.
    /// </summary>
    private static (ScanSize Size, bool WithheldRecent, IReadOnlyList<string> MailStores) MeasureFile(
        string path,
        MinimumAge keep,
        FileInformation files)
    {
        if (MailStore.Is(path))
        {
            return (Approximate(0, 0), false, [LongPath.Display(path)]);
        }

        if (keep.ProtectsFile(path))
        {
            return (Approximate(0, 0), true, []);
        }

        return (
            files.Standard(LongPath.Extended(path), new ByNameRoute()) is { } info
                ? Approximate(
                    info.NumberOfLinks == 1 ? info.AllocationSize : 0,
                    info.NumberOfLinks == 1 ? info.EndOfFile : 0)
                : Approximate(0, 0),
            false,
            []);
    }
}
