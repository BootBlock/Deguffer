using Deguffer.Core.Exploring;

namespace Deguffer.Benchmark;

/// <summary>
/// Progress nobody reads. An Explore read takes snapshots only where it has somewhere to report, so a
/// run that times them needs a sink, and each snapshot is assembled and dropped, as one a person never
/// sees would be.
/// </summary>
internal sealed class DiscardedProgress : IProgress<ExploreProgress>
{
    public static DiscardedProgress Instance { get; } = new();

    private DiscardedProgress()
    {
    }

    public void Report(ExploreProgress value)
    {
    }
}
