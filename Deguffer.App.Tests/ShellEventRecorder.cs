using System.Diagnostics.Tracing;
using Deguffer.App.Diagnostics;

namespace Deguffer.App.Tests;

/// <summary>
/// Records what <see cref="ShellEvents"/> raises on the thread that made the recorder, as a trace of
/// the running app would see it.
///
/// <para>Only that thread, because the source is one per process and the other tests go on raising
/// its events in parallel while a recorder enables it. An in-process listener is told on the thread
/// that wrote the event, which is how the two are told apart. Every test here runs its page on one
/// thread (<see cref="UiThread"/>), so nothing it raises is missed.</para>
/// </summary>
internal sealed class ShellEventRecorder : EventListener
{
    // Field initialisers run before the base constructor, which may already enable the source.
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly List<ShellEvent> _events = [];

    public IReadOnlyList<ShellEvent> Events => _events;

    /// <summary>The names of the events recorded, in the order raised.</summary>
    public IEnumerable<string> Names => _events.Select(raised => raised.Name);

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Deguffer-Shell")
        {
            EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (Environment.CurrentManagedThreadId == _thread && eventData.EventSource.Name == "Deguffer-Shell")
        {
            _events.Add(new ShellEvent(eventData.EventName!, [.. eventData.Payload ?? []]));
        }
    }
}

/// <summary>One event a <see cref="ShellEventRecorder"/> recorded.</summary>
internal sealed record ShellEvent(string Name, IReadOnlyList<object?> Payload)
{
    /// <summary>The payload as numbers, which every payload is: an enum is read as its value.</summary>
    public IEnumerable<int> Numbers => Payload.Select(value => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
}
