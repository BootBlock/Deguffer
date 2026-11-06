namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// How long each stage of one read of a file table took, and the counts that size the stages run
/// on one thread. Handed to a read by whatever measures it, and null in the app, so a read nobody
/// measures pays a null check at each stage boundary and nothing else.
///
/// <para>Every stage boundary is on the thread that started the read, so this takes no lock. Each
/// stage runs from where the one before it ended, so the stages add up to the read less what comes
/// before the first.</para>
///
/// <para>One instance times one read. A stage a read stopped before is absent, not zero.</para>
/// </summary>
/// <param name="clock">What the stages are timed by. The system's where null.</param>
public sealed class MftStageTimings(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly List<MftStageTime> _stages = [];

    private long _mark;

    /// <summary>Each stage that ran, in the order it ran.</summary>
    public IReadOnlyList<MftStageTime> Stages => _stages;

    /// <summary>How many records were held back for their extension records and handed on after the first pass.</summary>
    public int DeferredRecords { get; internal set; }

    /// <summary>How many of the files' other names were placed as nodes.</summary>
    public int LinksPlaced { get; internal set; }

    /// <summary>How long <paramref name="stage"/> took, or zero where it did not run.</summary>
    public TimeSpan Of(MftBuildStage stage)
    {
        foreach (var timed in _stages)
        {
            if (timed.Stage == stage)
            {
                return timed.Elapsed;
            }
        }

        return TimeSpan.Zero;
    }

    /// <summary>Start the clock for the first stage.</summary>
    internal void Start() => _mark = _clock.GetTimestamp();

    /// <summary>Record that <paramref name="stage"/> has ended, and start the clock for the next.</summary>
    internal void End(MftBuildStage stage)
    {
        var now = _clock.GetTimestamp();
        _stages.Add(new MftStageTime(stage, _clock.GetElapsedTime(_mark, now)));
        _mark = now;
    }
}
