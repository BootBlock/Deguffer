using Deguffer.Core.Cloud;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// Counts the placeholder descriptions in flight, and holds each of the first few that
/// <paramref name="holds"/> picks until <paramref name="wanted"/> are in flight or
/// <paramref name="patience"/> has passed, so the overlap a walk allows is observed rather than left
/// to chance, and overlap it must not allow has the time to show.
/// </summary>
/// <param name="holdsAtMost">How many descriptions are held, each for up to <paramref name="patience"/>.</param>
internal sealed class PlaceholderOverlapWatch(
    ICloudFiles inner, Func<string, bool> holds, int wanted, TimeSpan patience, int holdsAtMost = 4) : ICloudFiles
{
    private int _inFlight;
    private int _mostAtOnce;
    private int _held;

    public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

    public PlaceholderReading Read(string path)
    {
        var now = Interlocked.Increment(ref _inFlight);
        InterlockedMax(now);

        try
        {
            if (holds(path) && Interlocked.Increment(ref _held) <= holdsAtMost)
            {
                SpinWait.SpinUntil(() => MostAtOnce >= wanted, patience);
            }

            return inner.Read(path);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public IReadOnlyList<SyncRoot>? SyncRoots() => inner.SyncRoots();

    public VolumeDevicePath? Resolve(string path) => inner.Resolve(path);

    public SyncProviderState ProviderState(string syncRoot) => inner.ProviderState(syncRoot);

    public IEnumerable<CloudEntry> List(string directory, CancellationToken ct) => inner.List(directory, ct);

    public ReleaseAnswer Release(string path, VolumeDevicePath resolvedPath, Func<Placeholder, bool> stillEligible) =>
        inner.Release(path, resolvedPath, stillEligible);

    private void InterlockedMax(int value)
    {
        var seen = Volatile.Read(ref _mostAtOnce);
        while (value > seen)
        {
            var previous = Interlocked.CompareExchange(ref _mostAtOnce, value, seen);
            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
    }
}
