namespace Deguffer.Testing;

/// <summary>
/// A clock whose timestamp moves on by a fixed step at every read, so code that reports by time
/// reports on a cadence set by how often it looks, whatever the machine's load.
///
/// <para>Only the timestamp moves. The code it drives times its reports with
/// <see cref="TimeProvider.GetTimestamp"/>, and nothing it serves waits on a timer.</para>
/// </summary>
/// <param name="step">How far the timestamp moves at each read.</param>
public sealed class SteppingTimeProvider(TimeSpan step) : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Add(ref _ticks, step.Ticks);
}
