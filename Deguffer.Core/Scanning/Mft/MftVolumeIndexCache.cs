namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// One built index per volume, for the life of a scan.
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
/// it the offer of administrator rights.</para>
///
/// <para>A build belongs to the scan, not to whichever caller started it. A caller that stops
/// waiting, because the walk answered first, leaves the build running for the questions after it.
/// <see cref="Invalidate"/> ends every build, because the next scan starts from the machine as it is
/// then.</para>
/// </summary>
/// <param name="tuning">What each build reads the table with, asked as the build starts.</param>
public sealed class MftVolumeIndexCache(IMftSourceFactory factory, ScanTuner tuning)
{
    private readonly Dictionary<char, Lazy<Task<Built>>> _byVolume = [];
    private readonly Lock _gate = new();

    // Cancelled and replaced, never disposed. A build links its token to this one when it starts,
    // and disposing it under a build starting at that moment would throw from the link. It owns no
    // timer and no wait handle, so nothing is held by leaving it to the collector.
    private CancellationTokenSource _scan = new();

    /// <summary>The index for a volume, or null with the reason the fast path is unavailable for it.</summary>
    public readonly record struct Built(MftVolumeIndex? Index, FallbackReason Reason);

    /// <summary>
    /// The index for <paramref name="driveLetter"/>, started now if no build for it has been. Already
    /// complete where the volume would not open or the build has finished.
    /// </summary>
    public Task<Built> Get(char driveLetter)
    {
        var key = char.ToUpperInvariant(driveLetter);
        Lazy<Task<Built>> pending;

        // The lock covers only the dictionary; the Lazy serialises the open of the *same* volume,
        // which is the duplication worth preventing. Holding the lock across the open instead would
        // make a question about D: wait on a device that is slow to answer about C:.
        lock (_gate)
        {
            if (!_byVolume.TryGetValue(key, out pending!))
            {
                var scan = _scan.Token;
                pending = new Lazy<Task<Built>>(() => Start(key, scan), LazyThreadSafetyMode.ExecutionAndPublication);
                _byVolume[key] = pending;
            }
        }

        return pending.Value;
    }

    private Task<Built> Start(char driveLetter, CancellationToken scan)
    {
        // Asked before the open, so nothing between the open and the build can leave the handle
        // without an owner. The walk asks the same question of the same cache, so it costs nothing
        // where the open then fails.
        var table = tuning.ForVolume(driveLetter).Table;
        var source = factory.TryOpen(driveLetter, out var reason);

        if (source is null)
        {
            return Task.FromResult(new Built(null, reason));
        }

        // Never cancelled before it starts, so the volume handle is always closed by the build that
        // owns it. A build Invalidate ends stops between reads instead, with the handle in hand.
        return Task.Run(
            () =>
            {
                using (source)
                {
                    return Build(source, table, scan);
                }
            },
            CancellationToken.None);
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
    }

    /// <summary>
    /// Drop every index and end every build still running, so the next scan sees the machine as it
    /// is now.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _scan.Cancel();
            _scan = new CancellationTokenSource();
            _byVolume.Clear();
        }
    }
}
