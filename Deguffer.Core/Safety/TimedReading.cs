namespace Deguffer.Core.Safety;

/// <summary>
/// One reading of the machine that many questions are answered from, and when it began, so a caller
/// can ask that it be no older than a bound rather than either keep it for a whole pass or take it
/// again for every question.
///
/// <para><b>Aged from when the read began</b>, not from when it ended: a program started while the
/// process list was being walked is not in it, so the reading describes the machine as it was at its
/// start.</para>
///
/// <para>Not thread-safe by itself. Each owner reads and discards it under the lock that already
/// keeps its reading and what is derived from it consistent.</para>
/// </summary>
/// <typeparam name="T">The reading.</typeparam>
internal sealed class TimedReading<T>(TimeProvider time)
    where T : class
{
    private T? _reading;
    private long _began;

    /// <summary>
    /// How many readings have been taken, which names the one held: a reading dropped and taken again
    /// is a different number, so what an owner derives from one can be kept for exactly as long.
    /// </summary>
    public long Number { get; private set; }

    /// <summary>The reading held, or <paramref name="read"/>'s, which is then held.</summary>
    public T Get(Func<T> read)
    {
        if (_reading is { } held)
        {
            return held;
        }

        var began = time.GetTimestamp();
        var reading = read();
        _began = began;
        Number++;
        return _reading = reading;
    }

    /// <summary>Drop the reading, so the next question reads again.</summary>
    public void Forget() => _reading = null;

    /// <summary>
    /// Drop the reading where it began longer ago than <paramref name="maxAge"/>. True where one was
    /// dropped, so an owner can drop what it derived from it too.
    /// </summary>
    public bool ForgetOlderThan(TimeSpan maxAge)
    {
        if (_reading is null || time.GetElapsedTime(_began) <= maxAge)
        {
            return false;
        }

        _reading = null;
        return true;
    }
}
