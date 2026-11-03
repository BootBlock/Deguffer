using Deguffer.Core.Safety;

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

    /// <summary>The clock itself, for a removal inside the step to time its passes on.</summary>
    public TimeProvider Time => time;

    /// <summary>Now, for <see cref="UseCheckSince"/> to measure from.</summary>
    public long Mark() => time.GetTimestamp();

    public void UseCheckSince(long mark) => _useCheck += time.GetElapsedTime(mark);

    /// <summary>
    /// <see cref="WholeTreeLook.TakeAsync"/>, timed as the step's look. Every look a step takes goes
    /// through here, so a step that took one never reports none.
    /// </summary>
    public async Task<WholeTreeLook> LookAsync(IReadOnlyList<string> paths, MinimumAge keep, CancellationToken ct)
    {
        var started = time.GetTimestamp();

        try
        {
            return await WholeTreeLook.TakeAsync(paths, keep, ct).ConfigureAwait(false);
        }
        finally
        {
            _look += time.GetElapsedTime(started);
        }
    }

    public StepTiming Read() => new(time.GetElapsedTime(_started), _useCheck, _look);
}
