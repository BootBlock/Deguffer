using System.Reflection;
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

    /// <summary>
    /// Run <paramref name="callback"/> on <paramref name="owner"/> at each tick of <paramref name="timer"/>,
    /// recording a fault in it, for as long as anything else keeps the owner alive.
    ///
    /// <para>The timer is a native object that holds its handler with a plain COM reference, which the
    /// collector treats as a root and which WinUI's reference tracking does not see, so a handler that
    /// reached its owner would keep the owner, and everything it holds, alive for the life of the process
    /// (#737). The owner is held weakly, and the callback is handed it at each tick rather than capturing
    /// it, which is why it must capture nothing: a callback with state of its own is refused. A tick that
    /// finds the owner gone stops the timer.</para>
    /// </summary>
    public static void TickGuarded<TOwner>(this DispatcherQueueTimer timer, TOwner owner, Action<TOwner> callback)
        where TOwner : class
    {
        if (HoldsState(callback))
        {
            throw new ArgumentException(
                "A timer's callback is handed its owner at each tick, and must capture nothing: one that did would keep the owner alive for as long as the timer is.",
                nameof(callback));
        }

        var held = new WeakReference<TOwner>(owner);

        timer.Tick += (sender, _) =>
        {
            if (held.TryGetTarget(out var target))
            {
                Run(App.Faults, Ticked, (callback, target), static tick => tick.callback(tick.target));
            }
            else
            {
                sender.Stop();
            }
        };
    }

    /// <summary>
    /// Run <paramref name="callback"/>, and record a fault that escapes it in <paramref name="log"/>
    /// under <paramref name="source"/> before letting it continue unhandled.
    /// </summary>
    public static void Run(CrashLog log, string source, Action callback) =>
        Run(log, source, callback, static callback => callback());

    /// <summary>
    /// Run <paramref name="callback"/> on <paramref name="state"/>, and record a fault that escapes it
    /// in <paramref name="log"/> under <paramref name="source"/> before letting it continue unhandled.
    /// Each tick of a timer runs through it with no allocation.
    /// </summary>
    public static void Run<TState>(CrashLog log, string source, TState state, Action<TState> callback)
    {
        try
        {
            callback(state);
        }
        catch (Exception exception) when (RecordedAndDeclined(log, source, exception))
        {
            // Never entered: the filter always declines.
        }
    }

    /// <summary>
    /// Whether <paramref name="callback"/> reaches an object that holds anything. A static lambda's
    /// target is the compiler's one cached instance of a class with no instance fields, and a static
    /// method has none. A lambda that captures, or a method of an instance, has a target with fields.
    /// </summary>
    private static bool HoldsState(Delegate callback) =>
        callback.Target is { } target
        && target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length > 0;

    private static bool RecordedAndDeclined(CrashLog log, string source, Exception exception)
    {
        log.Record(source, exception);
        return false;
    }
}
