using Deguffer.Core.Providers;

namespace Deguffer.Core.Execution;

/// <summary>
/// The status line of a planning pass that checks several providers at once: which ones are being
/// checked now, oldest first, said again each time one starts or ends.
///
/// <para>Reported under a lock, so the line a caller shows last is the one that describes the pass
/// last. Without it two providers ending together could report out of order, and the line would go on
/// naming a provider that had already finished.</para>
/// </summary>
internal sealed class PlanningStatus(IProgress<string>? status)
{
    private readonly Lock _gate = new();
    private readonly List<ICleanupProvider> _checking = [];

    public void Started(ICleanupProvider provider)
    {
        lock (_gate)
        {
            _checking.Add(provider);
            ReportLocked();
        }
    }

    public void Ended(ICleanupProvider provider)
    {
        lock (_gate)
        {
            _checking.Remove(provider);

            // Nothing is said once the last one ends: the pass is over, and the caller's own sentence
            // about what it found replaces the line.
            if (_checking.Count > 0)
            {
                ReportLocked();
            }
        }
    }

    /// <summary>The line for <paramref name="checking"/>, the providers under way, oldest first.</summary>
    internal static string Describe(IReadOnlyList<ICleanupProvider> checking) => checking.Count switch
    {
        1 => $"Checking {checking[0].Name}…",
        2 => $"Checking {checking[0].Name} and {checking[1].Name}…",
        _ => $"Checking {checking[0].Name}, {checking[1].Name} and {checking.Count - 2} more…",
    };

    private void ReportLocked() => status?.Report(Describe(_checking));
}
