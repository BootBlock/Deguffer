using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// §5.5's fallback: measure a tree by walking it, with a bounded worker pool.
///
/// This is the approach the audit measured at over ten minutes on a handful of profile subtrees,
/// so it is explicitly *not* the scanner — it is what runs where the MFT cannot be read, and every
/// result it produces carries the reason (see <see cref="FallbackReason"/>).
///
/// Its sizes are approximate in one specific way: <c>FileInfo.Length</c> is the logical length, and
/// nothing here can see how many clusters a compressed or sparse file actually occupies. Learning
/// that would cost a <c>GetCompressedFileSize</c> call per file, on the path that is already the
/// slow one. <see cref="ScanSize.Approximate"/> records the compromise rather than hiding it.
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
            return ScanSize.Approximate(0);
        }

        long total = 0;

        BoundedFileWalk.Visit(
            LongPath.Extended(path),
            file => Interlocked.Add(ref total, file.Length),
            // §5.5: stream partial results. One report per breadth-first level, not per file.
            () => progress?.Report(ScanSize.Approximate(Interlocked.Read(ref total))),
            ct);

        return ScanSize.Approximate(Interlocked.Read(ref total));
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
                ? ScanSize.Approximate(file.Length)
                : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
