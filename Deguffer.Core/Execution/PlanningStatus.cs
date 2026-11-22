using Deguffer.Core.Providers;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Execution;

/// <summary>
/// The status line of a planning pass that checks several providers at once: which ones are being
/// checked now, oldest first, and the bytes each has counted so far, said again each time one starts
/// or ends or its figure moves.
///
/// <para><b>Reported by time, not by change</b> (<see cref="PacedReport"/>). Every start, end and moved
/// figure is recorded, but the line is built and posted to the UI thread at most once an interval, and
/// the latest line always follows the last change within one. A pass changes the line hundreds of times
/// in a few seconds, and each post sets the line and lays out the bar it is shown in.</para>
///
/// <para>Built and reported under a lock, so the line a caller shows last is the one that describes the
/// pass last. Without it two reports made together could post out of order, and the line would go on
/// naming a provider that had already finished. A figure reported for a provider that has ended is
/// dropped for the same reason. The lock is taken once a change to record it, and the line is built only
/// when a report is due, so the slots do not wait on each other's formatting.</para>
///
/// <para><b>The figure is a count of work done, and is worded as one.</b> §5.5 asks for partial results
/// rather than a wait on a complete scan, and a walk of a large location is the wait. A running figure
/// falls short of the finished one and can include a path the plan then offers less of, so it is never
/// called what a clean would free, and the finding's own figure replaces it once the provider ends.
/// The line is not announced as it changes: the info bar it is shown in speaks when it opens, and a
/// screen reader reads the current figure when asked, rather than ten times a second.</para>
/// </summary>
internal sealed class PlanningStatus : IDisposable
{
    private readonly IProgress<string>? _status;
    private readonly PacedReport _paced;
    private readonly Lock _gate = new();
    private readonly List<Underway> _checking = [];

    /// <summary>The line last reported, so a figure that moved without changing the line says nothing.</summary>
    private string? _said;

    /// <param name="status">Where the line goes, or null where nobody is watching.</param>
    /// <param name="time">What times the reports: the seam a test reports through.</param>
    public PlanningStatus(IProgress<string>? status, TimeProvider time)
    {
        _status = status;
        _paced = new PacedReport(time, Report);
    }

    public void Started(ICleanupProvider provider)
    {
        lock (_gate)
        {
            _checking.Add(new Underway(provider));
        }

        _paced.Changed();
    }

    public void Ended(ICleanupProvider provider)
    {
        lock (_gate)
        {
            _checking.RemoveAll(underway => ReferenceEquals(underway.Provider, provider));
        }

        _paced.Changed();
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

    /// <summary>Report nothing more: the pass is over, and the caller's own sentence replaces the line.</summary>
    public void Dispose() => _paced.Dispose();

    private void Count(ICleanupProvider provider, ScanSize counted)
    {
        lock (_gate)
        {
            if (_checking.Find(underway => ReferenceEquals(underway.Provider, provider)) is not { } underway)
            {
                return;
            }

            underway.Counted = counted.Reclaimable;
        }

        _paced.Changed();
    }

    private void Report()
    {
        lock (_gate)
        {
            // Nothing is said once the last one ends: the pass is over, and the caller's own sentence
            // about what it found replaces the line. A report due as the last one ended finds none.
            if (_checking.Count == 0)
            {
                return;
            }

            var line = Describe(_checking);

            if (line != _said)
            {
                _said = line;
                _status?.Report(line);
            }
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
