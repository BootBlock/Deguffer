using Microsoft.UI.Dispatching;

namespace Deguffer.App.Shell;

/// <summary>
/// Runs an action once, an interval after the first of any number of pokes, and again an interval after
/// the first poke that follows. A burst of pokes runs it at most once an interval, and the last poke of
/// a burst is never left without a run after it.
/// </summary>
internal sealed class Throttle
{
    private readonly DispatcherQueueTimer _timer;
    private readonly Action _action;

    /// <param name="queue">The queue of the thread the action runs on, and that every poke comes from.</param>
    /// <param name="interval">How long after the first poke the action runs.</param>
    /// <param name="action">What runs.</param>
    public Throttle(DispatcherQueue queue, TimeSpan interval, Action action)
    {
        _action = action;
        _timer = queue.CreateTimer();
        _timer.Interval = interval;
        _timer.IsRepeating = false;
        _timer.TickGuarded(this, static throttle => throttle._action());
    }

    /// <summary>Ask for a run, which is the one already on its way where there is one.</summary>
    public void Poke()
    {
        if (!_timer.IsRunning)
        {
            _timer.Start();
        }
    }

    /// <summary>Call off the run on its way, if there is one.</summary>
    public void Stop() => _timer.Stop();
}
