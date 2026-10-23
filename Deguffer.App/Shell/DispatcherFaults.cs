using Deguffer.Core.Diagnostics;
using Microsoft.UI.Dispatching;

namespace Deguffer.App.Shell;

/// <summary>
/// Records a fault in work the app queues on the dispatcher before the process ends.
///
/// <para>An exception that escapes a <see cref="DispatcherQueue"/> callback or a
/// <see cref="DispatcherQueueTimer"/> tick fails fast across the WinRT boundary without reaching any
/// managed handler (measured for both: <c>0xc000027b</c>, and nothing recorded), so none of
/// <see cref="FaultReporting"/>'s handlers sees it and <c>crash.log</c> is left empty. Every
/// callback the app queues and every tick of a <see cref="DispatcherQueueTimer"/> runs through
/// <see cref="Run"/> instead.</para>
///
/// <para>Two neighbours need none of this, because a fault in either was measured reaching
/// <c>Application.UnhandledException</c>: the tick of a XAML <c>DispatcherTimer</c>, and a post
/// through <see cref="DispatcherQueueSynchronizationContext"/>, which catches what it runs and
/// raises it there.</para>
///
/// <para>The fault is recorded from an exception filter that then declines it, so it is written before
/// anything unwinds and the process still ends at the frame that threw, as it would unguarded. Nothing
/// is caught: a fault from an unknown point leaves the app in an unknown state, and this tool deletes
/// directories.</para>
/// </summary>
internal static class DispatcherFaults
{
    public const string Enqueued = "DispatcherQueue.TryEnqueue";

    public const string Ticked = "DispatcherQueueTimer.Tick";

    /// <summary>Queue <paramref name="callback"/>, recording a fault in it.</summary>
    public static bool TryEnqueueGuarded(this DispatcherQueue queue, Action callback) =>
        queue.TryEnqueue(() => Run(App.Faults, Enqueued, callback));

    /// <summary>Queue <paramref name="callback"/> at <paramref name="priority"/>, recording a fault in it.</summary>
    public static bool TryEnqueueGuarded(this DispatcherQueue queue, DispatcherQueuePriority priority, Action callback) =>
        queue.TryEnqueue(priority, () => Run(App.Faults, Enqueued, callback));

    /// <summary>Run <paramref name="callback"/> on each tick of <paramref name="timer"/>, recording a fault in it.</summary>
    public static void TickGuarded(this DispatcherQueueTimer timer, Action callback) =>
        timer.Tick += (_, _) => Run(App.Faults, Ticked, callback);

    /// <summary>
    /// Run <paramref name="callback"/>, and record a fault that escapes it in <paramref name="log"/>
    /// under <paramref name="source"/> before letting it continue unhandled.
    /// </summary>
    public static void Run(CrashLog log, string source, Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception exception) when (RecordedAndDeclined(log, source, exception))
        {
            // Never entered: the filter always declines.
        }
    }

    private static bool RecordedAndDeclined(CrashLog log, string source, Exception exception)
    {
        log.Record(source, exception);
        return false;
    }
}
