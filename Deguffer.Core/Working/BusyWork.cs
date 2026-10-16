namespace Deguffer.Core.Working;

/// <summary>
/// What the app is working on right now, on every page: scans, searches, cleans and removals, each
/// with how far it has got where it knows.
///
/// <para>Wider than <see cref="Execution.RunningActions"/>, which holds only what changes the
/// machine, because it answers a different question. That one decides whether the process may end,
/// and a scan never stops it. This one says whether anything is under way at all, for the taskbar
/// button (<see cref="TaskbarIndicator"/>), and a scan is the longest thing the app does.</para>
///
/// <para>A page begins a span where its own busy indicator comes on and disposes it where that goes
/// off, so the taskbar says what the page says. Every span that is never disposed holds the taskbar
/// busy for the life of the process, so a span is ended in the same <c>finally</c> as the page's own
/// busy flag, which already covers every way out of the run.</para>
///
/// <para>Used from the window's thread only, as <see cref="Execution.RunningActions"/> is. A
/// <see cref="Progress{T}"/> callback posts to that thread, so a report from a worker arrives there
/// too, and can arrive after its span has ended, which <see cref="BusySpan"/> allows for.</para>
/// </summary>
public sealed class BusyWork
{
    private readonly List<BusySpan> _spans = [];

    /// <summary>Raised after a span begins, moves or ends.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when a span's work fails, before the span ends.</summary>
    public event EventHandler? Failed;

    /// <summary>Whether nothing is under way.</summary>
    public bool IsIdle => _spans.Count == 0;

    /// <summary>What is under way, oldest first.</summary>
    public IReadOnlyList<BusySpan> Current => [.. _spans];

    /// <summary>
    /// Record that work has begun, with no measure yet of how far it has got. Disposing the result
    /// records that it has ended, and a second disposal does nothing.
    /// </summary>
    public BusySpan Begin()
    {
        var span = new BusySpan(this);
        _spans.Add(span);
        Changed?.Invoke(this, EventArgs.Empty);

        return span;
    }

    internal void Moved() => Changed?.Invoke(this, EventArgs.Empty);

    internal void Fail() => Failed?.Invoke(this, EventArgs.Empty);

    internal void End(BusySpan span)
    {
        _spans.Remove(span);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
