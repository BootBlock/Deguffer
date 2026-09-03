using System.Diagnostics;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Mft;

namespace Deguffer.Core.Exploring;

/// <summary>
/// The one way anything learns what a whole volume holds.
///
/// <para>Choosing between §5.5's two routes lives here and nowhere else, exactly as it does in
/// <see cref="DirectoryScanner"/> for a single path. Nothing above this knows there is a choice to
/// make, because the choice depends on the volume and the process token rather than on anything
/// about what is being drawn (G1, G2).</para>
///
/// <para>Concrete, with no interface over it. The seam that makes this testable is
/// <see cref="IMftSourceFactory"/>, which already exists and which the tests substitute directly;
/// an interface here would have one implementation, no fake behind it, and one consumer — which is
/// the ceremonial abstraction G3 names. <c>CleanViewModel</c> takes its <c>CleanupPlanner</c> the
/// same way.</para>
/// </summary>
public sealed class ExploreScanner(IMftSourceFactory? sources = null)
{
    /// <summary>
    /// How often the walk publishes a tree to draw.
    ///
    /// <para>A snapshot copies every array, so it is not free — and the reason to take one at all is
    /// that a scan of a full drive is long enough that an unchanging window reads as a hung one.
    /// Three quarters of a second is slow enough that the copy is noise against the enumeration and
    /// fast enough to look alive.</para>
    ///
    /// <para>It is also as far as this goes deliberately. Every disk tool surveyed for this feature
    /// — WinDirStat, KDirStat, QDirStat, Filelight, Baobab, GrandPerspective, Disk Inventory X —
    /// refuses to draw its map until the scan finishes, and DaisyDisk tried live rings and abandoned
    /// them. The measured reason is layout instability: on the Bederson/Shneiderman/Wattenberg
    /// change metric a squarified treemap scores 14.82 against slice-and-dice's 0.25, so a map
    /// redrawn from growing data rearranges itself continuously. Deguffer publishes the snapshots
    /// and leaves what to do with them to the view.</para>
    /// </summary>
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(750);

    private readonly IMftSourceFactory _sources = sources ?? VolumeMftSourceFactory.Default;

    /// <summary>The scanner the app runs with: real volumes (G5).</summary>
    public static ExploreScanner Default { get; } = new();

    /// <summary>
    /// Scan everything at or below <paramref name="root"/>.
    ///
    /// <paramref name="progress"/> receives running counts, and occasionally a snapshot of the tree
    /// so far — §5.5: never block on a complete scan.
    /// </summary>
    public async ValueTask<ExploreScan> ScanAsync(
        string root,
        IProgress<ExploreProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        // Both routes are synchronous and long: reading a table is millions of records, and the
        // walk enumerates before its first yield. On the caller's thread that is a frozen window
        // for the length of the scan, which is the one thing §5.5 asks a scanner not to be.
        return await Task.Run(() => Scan(root, progress, ct), ct).ConfigureAwait(false);
    }

    private ExploreScan Scan(string root, IProgress<ExploreProgress>? progress, CancellationToken ct)
    {
        if (!VolumePath.TryParse(root, out var volume))
        {
            return Walk(root, FallbackReason.VolumeNotAddressable, progress, ct);
        }

        // The table is addressed from a volume's own root, so a scan starting below one takes the
        // walk because that is what it is for — not because anything was unavailable. Rooting the
        // table at a subtree is the work that makes folder scoping worth having, and it is not this
        // change. FallbackReason.None is how the difference reaches the user: no route note, and no
        // offer of administrator rights that would not change the answer.
        if (volume.Components.Count > 0)
        {
            return Walk(root, FallbackReason.None, progress, ct);
        }

        var source = _sources.TryOpen(volume.DriveLetter, out var reason);
        if (source is null)
        {
            return Walk(root, reason, progress, ct);
        }

        using (source)
        {
            try
            {
                return ExploreScan.Fast(Read(source, root, progress, ct));
            }
            catch (IOException)
            {
                // The volume went away mid-scan, or the driver refused a read. Neither should take
                // the window down, and the walk still answers.
            }
        }

        // Outside the using deliberately. The walk can run for minutes on a full drive, and holding
        // a raw volume handle open across it serves nothing once the table has been given up on.
        return Walk(root, FallbackReason.MasterFileTableIncomplete, progress, ct);
    }

    private static ExploreTree Read(
        IMftSource source,
        string root,
        IProgress<ExploreProgress>? progress,
        CancellationToken ct)
    {
        var total = source.RecordCount;

        // No snapshot from this route, and that is not a shortcut. The parent links are inverted
        // once, after every record has been read, so there is no partial tree to hand over — and
        // the pass it would interrupt is the whole cost of the route.
        return MftExploreReader.Read(
            source,
            root,
            done => progress?.Report(new ExploreProgress(done, total, BytesSeen: 0)),
            ct);
    }

    private static ExploreScan Walk(
        string root,
        FallbackReason reason,
        IProgress<ExploreProgress>? progress,
        CancellationToken ct)
    {
        var since = Stopwatch.StartNew();

        var tree = WalkExploreReader.Read(
            root,
            (builder, items, bytes) =>
            {
                if (progress is null)
                {
                    return;
                }

                var due = since.Elapsed >= SnapshotInterval;
                if (due)
                {
                    since.Restart();
                }

                progress.Report(new ExploreProgress(items, Total: null, bytes, due ? builder.Build() : null));
            },
            ct);

        return ExploreScan.Walked(tree, reason);
    }
}
