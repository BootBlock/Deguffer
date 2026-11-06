namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// How long each step of one open of a volume's file table took, and how many reads each made.
/// Handed to an open by whatever measures it, and null in the app, so an open nobody measures pays a
/// null check at each step boundary and each read, and nothing else.
///
/// <para>An open runs on one thread, so this takes no lock. Each step runs from where the one before
/// it ended, so the steps add up to the open. One instance times one open. A step the open stopped
/// before is absent, not zero.</para>
/// </summary>
/// <param name="clock">What the steps are timed by. The system's where null.</param>
public sealed class MftOpenTimings(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly List<MftOpenStepTime> _steps = [];

    private long _mark;

    private int _reads;

    /// <summary>Each step that ran, in the order it ran.</summary>
    public IReadOnlyList<MftOpenStepTime> Steps => _steps;

    /// <summary>How <paramref name="step"/> went, or null where it did not run.</summary>
    public MftOpenStepTime? Of(MftOpenStep step)
    {
        foreach (var timed in _steps)
        {
            if (timed.Step == step)
            {
                return timed;
            }
        }

        return null;
    }

    /// <summary>Start the clock for the first step.</summary>
    internal void Start() => _mark = _clock.GetTimestamp();

    /// <summary>Count a read against the step running now.</summary>
    internal void Read() => _reads++;

    /// <summary>Record that <paramref name="step"/> has ended, and start the clock and the counts for the next.</summary>
    internal void End(MftOpenStep step)
    {
        var now = _clock.GetTimestamp();
        _steps.Add(new MftOpenStepTime(step, _clock.GetElapsedTime(_mark, now), _reads));
        _mark = now;
        _reads = 0;
    }
}
