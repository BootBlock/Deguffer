using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// The status line of a planning pass that checks several providers at once: which ones are being
/// checked now, oldest first, and the bytes each has counted so far, said again each time one starts
/// or ends or its figure moves.
///
/// <para>Reported under a lock, so the line a caller shows last is the one that describes the pass
/// last. Without it two providers ending together could report out of order, and the line would go on
/// naming a provider that had already finished. A figure reported for a provider that has ended is
/// dropped for the same reason.</para>
///
/// <para><b>The figure is a count of work done, and is worded as one.</b> §5.5 asks for partial results
/// rather than a wait on a complete scan, and a walk of a large location is the wait. A running figure
/// falls short of the finished one and can include a path the plan then offers less of, so it is never
/// called what a clean would free, and the finding's own figure replaces it once the provider ends.
/// The line is not announced as it changes: the info bar it is shown in speaks when it opens, and a
/// screen reader reads the current figure when asked, rather than ten times a second.</para>
/// </summary>
internal sealed class PlanningStatus(IProgress<string>? status)
{
    private readonly Lock _gate = new();
    private readonly List<Underway> _checking = [];

    /// <summary>The line last reported, so a figure that moved without changing the line says nothing.</summary>
    private string? _said;

    public void Started(ICleanupProvider provider)
    {
        lock (_gate)
        {
            _checking.Add(new Underway(provider));
            ReportLocked();
        }
    }

    public void Ended(ICleanupProvider provider)
    {
        lock (_gate)
        {
            _checking.RemoveAll(underway => ReferenceEquals(underway.Provider, provider));

            // Nothing is said once the last one ends: the pass is over, and the caller's own sentence
            // about what it found replaces the line.
            if (_checking.Count > 0)
            {
                ReportLocked();
            }
        }
    }

    /// <summary>Where <paramref name="provider"/>'s plan reports the bytes it has counted so far.</summary>
    public IProgress<ScanSize> Counted(ICleanupProvider provider) => new CountedBy(this, provider);

    /// <summary>The line for <paramref name="checking"/>, the providers under way, oldest first.</summary>
    internal static string Describe(IReadOnlyList<Underway> checking) => checking.Count switch
    {
        1 => $"Checking {checking[0].Text}…",
        2 => $"Checking {checking[0].Text} and {checking[1].Text}…",
        _ => $"Checking {checking[0].Text}, {checking[1].Text} and {checking.Count - 2} more…",
    };

    private void Count(ICleanupProvider provider, ScanSize counted)
    {
        lock (_gate)
        {
            if (_checking.Find(underway => ReferenceEquals(underway.Provider, provider)) is { } underway)
            {
                underway.Counted = counted.Reclaimable;
                ReportLocked();
            }
        }
    }

    private void ReportLocked()
    {
        var line = Describe(_checking);

        if (line != _said)
        {
            _said = line;
            status?.Report(line);
        }
    }

    /// <summary>One provider being checked, and the bytes its plan has counted so far.</summary>
    internal sealed class Underway(ICleanupProvider provider)
    {
        public ICleanupProvider Provider { get; } = provider;

        public long Counted { get; set; }

        /// <summary>The provider's name, with its figure once it has counted anything.</summary>
        public string Text =>
            Counted > 0 ? $"{Provider.Name} ({FreeSpace.Format(Counted)} counted so far)" : Provider.Name;
    }

    private sealed class CountedBy(PlanningStatus status, ICleanupProvider provider) : IProgress<ScanSize>
    {
        public void Report(ScanSize value) => status.Count(provider, value);
    }
}
