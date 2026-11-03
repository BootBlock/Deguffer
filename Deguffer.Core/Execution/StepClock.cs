namespace Deguffer.Core.Execution;

/// <summary>
/// Times one step as <see cref="PlanExecutor"/> runs it. Started where the step starts, told where each
/// wait began, and read once the step has an outcome. See <see cref="StepTiming"/>.
/// </summary>
internal sealed class StepClock(TimeProvider time)
{
    private readonly long _started = time.GetTimestamp();
    private TimeSpan _useCheck;
    private TimeSpan _look;

    /// <summary>Now, for one of the methods below to measure from.</summary>
    public long Mark() => time.GetTimestamp();

    public void UseCheckSince(long mark) => _useCheck += time.GetElapsedTime(mark);

    public void LookSince(long mark) => _look += time.GetElapsedTime(mark);

    public StepTiming Read() => new(time.GetElapsedTime(_started), _useCheck, _look);
}
