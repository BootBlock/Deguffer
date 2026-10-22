using System.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Deguffer.App.Views;

/// <summary>
/// The clock a play of the replay of what grew runs on (#285): a tick on each step for as long as the
/// play goes on, at the pace the replay asks for (<see cref="ExploreReplay.Pace"/>).
///
/// <para>Apart from the view model, which says where a play goes and when it is over, so that can be
/// tested without a dispatcher. This only keeps time. A play stops as the page leaves the screen,
/// because nobody is watching it, and the reader's Animation effects setting is followed while the
/// page is on screen, because a move of the scrubber goes another distance with it.</para>
/// </summary>
internal sealed class ExploreReplayClock
{
    private readonly ExploreReplay _replay;

    private readonly DispatcherQueueTimer _ticks;

    public ExploreReplayClock(ExploreReplay replay, FrameworkElement page)
    {
        _replay = replay;
        _ticks = page.DispatcherQueue.CreateTimer();
        _ticks.IsRepeating = true;
        _ticks.TickGuarded(Tick);

        replay.PropertyChanged += OnReplayChanged;

        page.Loaded += (_, _) => SystemMotion.Current.Changed += OnMotionChanged;
        page.Unloaded += (_, _) =>
        {
            SystemMotion.Current.Changed -= OnMotionChanged;
            replay.Pause();
        };
    }

    private void OnReplayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ExploreReplay.IsPlaying))
        {
            return;
        }

        if (_replay.IsPlaying)
        {
            _ticks.Interval = _replay.Pace;
            _ticks.Start();
        }
        else
        {
            _ticks.Stop();
        }
    }

    private void Tick()
    {
        if (_replay.Advance())
        {
            // Asked again at each step, as the setting can change part way through a play.
            _ticks.Interval = _replay.Pace;
        }
    }

    private void OnMotionChanged(object? sender, EventArgs e) => _replay.Restate();
}
