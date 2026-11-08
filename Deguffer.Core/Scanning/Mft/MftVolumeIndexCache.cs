using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// One built index per volume, for the life of a planning pass.
///
/// G4/G5: building the index is the whole cost of the fast path, and every provider asks about
/// paths on the same volume. Building it per query would make the MFT route slower than the walk
/// it replaces. Failures are remembered too — an unelevated process would otherwise attempt, and
/// lose, a volume open for every path measured.
///
/// <para><b>Built in the background.</b> <see cref="Get"/> opens the volume on the caller's thread
/// and hands back the build still running, so a caller can walk while it waits, as
/// <see cref="Configuration.ScanRoute.Auto"/> does. The open is not deferred with the build, because
/// it is what says the table cannot be read at all, and a caller racing the walk has to know that
/// before it starts: a walk that happened to finish first would otherwise hide the reason, and with
/// it the offer of administrator rights. The read of where the table is, which the open leaves to
/// <see cref="IMftVolume.TryReadTable"/>, is deferred: its first read makes NTFS flush the whole
/// volume, which took 0.8 to 1.6 s after a walk where it was measured (#544), and the walk raced against the build would
/// otherwise wait for it before it started. A table that cannot be found is then a failure of the
/// build, as one that cannot be parsed whole already is: a walk that answers before it is known says
/// nothing, and every question after it carries the reason.</para>
///
/// <para>A build belongs to the scan, not to whichever caller started it. A caller that stops
/// waiting, because the walk answered first, leaves the build running for the questions after it.
/// <see cref="Invalidate"/> ends every build, because the next scan starts from the machine as it is
/// then.</para>
///
/// <para><b>Read at low priority while a walk is in flight on its disk.</b> Under Auto a build reads
/// its table after the walks <see cref="Walking"/> registers on its disks, until <see cref="Need"/>
/// says a caller waits for it alone: see <see cref="BuildPriorities"/>.</para>
///
/// <para><b>Kept only while a pass holds it.</b> An index costs about 49 bytes for every slot of the
/// volume's table, so a system volume of a few million slots holds over a hundred megabytes. Nothing
/// reads it once the pass that built it has ended, because the next pass starts with
/// <see cref="Invalidate"/>. So the last <see cref="Hold"/> to end drops every index and ends every
/// build, as <see cref="Invalidate"/> does. The holds are counted rather than paired, because every
/// provider of a pass takes one on the scanner they share, and the first provider to give its hold
/// back must not take the index from the rest.</para>
/// </summary>
public sealed class MftVolumeIndexCache
{
    private readonly IMftSourceFactory _factory;
    private readonly ScanTuner _tuning;
    private readonly Action<Action> _run;
    private readonly Dictionary<char, (Lazy<Task<Built>> Build, BuildPriorities.Build Priority)> _byVolume = [];
    private readonly BuildPriorities _priorities = new();
    private readonly Lock _gate = new();
    private int _holds;

    // Cancelled and replaced, never disposed. A build links its token to this one when it starts,
    // and disposing it under a build starting at that moment would throw from the link. It owns no
    // timer and no wait handle, so nothing is held by leaving it to the collector.
    private CancellationTokenSource _scan = new();

    /// <param name="tuning">What each build reads the table with, asked as the build starts.</param>
    public MftVolumeIndexCache(IMftSourceFactory factory, ScanTuner tuning)
        : this(factory, tuning, WorkerThreads.Shared.Run)
    {
    }

    /// <param name="run">Starts a build on a thread of its own: the seam a test fails a start through.</param>
    internal MftVolumeIndexCache(IMftSourceFactory factory, ScanTuner tuning, Action<Action> run)
    {
        _factory = factory;
        _tuning = tuning;
        _run = run;
    }

    /// <summary>The index for a volume, or null with the reason the fast path is unavailable for it.</summary>
    public readonly record struct Built(MftVolumeIndex? Index, FallbackReason Reason);

    /// <summary>
    /// The index for <paramref name="driveLetter"/>, started now if no build for it has been. Already
    /// complete where the volume would not open or the build has finished.
    /// </summary>
    public Task<Built> Get(char driveLetter) => Entry(driveLetter).Build.Value;

    /// <summary>
    /// A caller waits for the index of <paramref name="driveLetter"/> with no walk beside it, so the
    /// build is not speculative and reads at normal priority for the rest of its life. See
    /// <see cref="BuildPriorities"/>.
    ///
    /// <para>Kept for a build not started yet, which then never yields, so a caller can say it waits
    /// alone before the question that starts the build. Starts nothing and opens nothing.</para>
    /// </summary>
    public void Need(char driveLetter) => _priorities.Need(Entry(driveLetter).Priority);

    /// <summary>
    /// A walk of <paramref name="paths"/> is in flight until the walk returned is disposed, and every
    /// build on the disks under them reads after it. See <see cref="BuildPriorities"/>.
    /// </summary>
    public IDisposable Walking(IEnumerable<string> paths) => _priorities.Walking(DisksOf(paths));

    /// <summary>
    /// The physical disks under the volumes holding <paramref name="paths"/>, or null where any of them
    /// could not say, which <see cref="BuildPriorities"/> takes to share every disk.
    ///
    /// <para>Asked of each path rather than its drive letter, so a folder mounted from another volume
    /// counts that volume's disks. A share, a mapped network drive or an optical drive is on no disk of
    /// this machine, which its media says with no reason, and adds no disk rather than every one.</para>
    /// </summary>
    private IReadOnlyList<int>? DisksOf(IEnumerable<string> paths)
    {
        var disks = new SortedSet<int>();

        foreach (var path in paths)
        {
            switch (_tuning.StorageOf(path))
            {
                case { PhysicalDisks.Count: > 0 } media:
                    disks.UnionWith(media.PhysicalDisks);
                    break;

                case { Reason: MediaUnknownReason.None }:
                    break;

                default:
                    return null;
            }
        }

        return [.. disks];
    }

    /// <summary>The volume's build, started by the first read of its <c>Build</c>, and its priority.</summary>
    private (Lazy<Task<Built>> Build, BuildPriorities.Build Priority) Entry(char driveLetter)
    {
        var key = char.ToUpperInvariant(driveLetter);

        // The lock covers only the dictionary; the Lazy serialises the open of the *same* volume,
        // which is the duplication worth preventing. Holding the lock across the open instead would
        // make a question about D: wait on a device that is slow to answer about C:.
        lock (_gate)
        {
            if (!_byVolume.TryGetValue(key, out var entry))
            {
                var scan = _scan.Token;
                var priority = new BuildPriorities.Build();
                entry = (new Lazy<Task<Built>>(() => Start(key, priority, scan), LazyThreadSafetyMode.ExecutionAndPublication), priority);
                _byVolume[key] = entry;
            }

            return entry;
        }
    }

    private Task<Built> Start(char driveLetter, BuildPriorities.Build priority, CancellationToken scan)
    {
        // Asked before the open, so nothing between the open and the build can leave the handle
        // without an owner. The walk asks the same question of the same cache, so it costs nothing
        // where the open then fails.
        var table = _tuning.ForVolume(driveLetter).Table;
        var volume = _factory.TryOpen(driveLetter, out var reason);

        if (volume is null)
        {
            return Task.FromResult(new Built(null, reason));
        }

        // Only under Auto, the one route that races the questions it asks a build against a walk.
        // File table first waits for the table, so its build is the answer and keeps normal priority.
        if (_tuning.Route is Configuration.ScanRoute.Auto)
        {
            _priorities.Yield(priority, volume, DisksOf([$@"{driveLetter}:\"]));
        }

        // A build Invalidate or the last hold ends stops at its next check of the token, with the handle
        // in hand, and ends cancelled rather than faulted. Its index is dropped by then, so nothing
        // awaits it, and a fault nobody observes is reported as a crash when the collector finds it.
        // Not on the pool: the build holds its thread for the whole pass, and a preview can start one
        // for every volume at once.
        var ran = false;
        var built = WorkerThreads.RunAsync(
            _run,
            () =>
            {
                Volatile.Write(ref ran, true);

                // Read here rather than with the open: see the class remarks. A table that cannot be
                // found closes the volume and says why, as an open that fails does.
                using var source = volume.TryReadTable(out var unread);

                return source is null
                    ? new Built(null, unread)
                    : Build(source, table, scan);
            },
            scan);

        // A thread the system would not start, or a scan ended before the build began, never ran the
        // build, and the build is all that closes the handle. A build that ran closed it on the way out.
        if ((built.IsFaulted || built.IsCanceled) && !Volatile.Read(ref ran))
        {
            volume.Dispose();
        }

        return built;
    }

    private static Built Build(IMftSource source, TableTuning tuning, CancellationToken scan)
    {
        try
        {
            return MftVolumeIndexBuilder.TryBuild(source, tuning, out var index, scan)
                ? new Built(index, FallbackReason.None)
                : new Built(null, FallbackReason.MasterFileTableIncomplete);
        }
        catch (IOException)
        {
            // The volume went away mid-scan, or the driver refused a read. Both mean this
            // volume takes the slow route; neither should take the preview down.
            return new Built(null, FallbackReason.MasterFileTableIncomplete);
        }
        catch (OperationCanceledException stopped) when (scan.IsCancellationRequested && stopped.CancellationToken != scan)
        {
            // A read inside the build stops on a token of its own, linked to the scan's, and the thread
            // the build runs on ends its task cancelled only for the scan's token. Stated again on that
            // token, so a build stops cancelled at whatever stage it was stopped in.
            throw new OperationCanceledException(stopped.Message, stopped, scan);
        }
    }

    /// <summary>
    /// Keep every index built from now until the hold ends, and drop them all when the last hold
    /// still standing ends. See the class remarks.
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
                Drop();
            }
        }
    }

    /// <summary>
    /// Drop every index and end every build still running, so the next scan sees the machine as it
    /// is now.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            Drop();
        }
    }

    private void Drop()
    {
        _scan.Cancel();
        _scan = new CancellationTokenSource();
        _byVolume.Clear();
        _priorities.Forget();
    }
}
