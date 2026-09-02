using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// §5.5's fallback: measure a tree by walking it, with a bounded worker pool.
///
/// This is the approach the audit measured at over ten minutes on a handful of profile subtrees,
/// so it is explicitly *not* the scanner — it is what runs where the MFT cannot be read, and every
/// result it produces carries the reason (see <see cref="FallbackReason"/>).
///
/// It reports file lengths and nothing else. <c>FileInfo.Length</c> cannot see how many clusters a
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
        IProgress<ScanSize>? progress = null,
        CancellationToken ct = default) =>
        new(Task.Run(
            () => TryMeasureFile(path) is { } file
                ? ScanResult.Direct(file)
                : ScanResult.Slow(Measure(path, progress, ct), _reason),
            ct));

    /// <summary>Nothing is remembered here, so every reading is already taken from the disk.</summary>
    public ValueTask<ScanResult> MeasureFromDiskAsync(string path, CancellationToken ct = default) =>
        MeasureAsync(path, progress: null, ct);

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

    private static ScanSize Measure(string path, IProgress<ScanSize>? progress, CancellationToken ct)
    {
        if (!LongPath.DirectoryExists(path))
        {
            return ScanSize.FromLengths(0);
        }

        long total = 0;

        BoundedFileWalk.Visit(
            LongPath.Extended(path),
            file => Interlocked.Add(ref total, file.Length),
            // §5.5: stream partial results. One report per breadth-first level, not per file.
            () => progress?.Report(ScanSize.FromLengths(Interlocked.Read(ref total))),
            ct);

        return ScanSize.FromLengths(Interlocked.Read(ref total));
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
    private static ScanSize? TryMeasureFile(string path)
    {
        try
        {
            var file = new FileInfo(LongPath.Extended(path));

            // A link's length is its own, not its target's, and following one would count a tree
            // this scanner never looked inside — the same rule BoundedFileWalk applies to every
            // entry it enumerates.
            return file.Exists && !file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                ? ScanSize.FromLengths(file.Length)
                : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
