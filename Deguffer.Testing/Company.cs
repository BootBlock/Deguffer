namespace Deguffer.Testing;

/// <summary>
/// A place work waits for other work, which counts the most that waited there at once
/// (<see cref="MostAtOnce"/>). Two at once is only possible where two pieces of work ran together.
///
/// <para>Where two must overlap, wait long enough that only a second that never starts fails the
/// test (#395). Where they must not, wait briefly: each piece that is rightly alone pays the wait in
/// full, and a second started later than that is not seen.</para>
/// </summary>
public sealed class Company
{
    private int _here;
    private int _mostAtOnce;

    /// <summary>The most pieces of work that waited here at once.</summary>
    public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

    /// <summary>Wait here until other work arrives or <paramref name="wait"/> passes.</summary>
    public void Wait(TimeSpan wait)
    {
        Raise(Interlocked.Increment(ref _here));
        SpinWait.SpinUntil(() => Volatile.Read(ref _here) > 1, wait);
        Raise(Volatile.Read(ref _here));
        Interlocked.Decrement(ref _here);
    }

    private void Raise(int atOnce)
    {
        var seen = Volatile.Read(ref _mostAtOnce);

        while (atOnce > seen && Interlocked.CompareExchange(ref _mostAtOnce, atOnce, seen) is var was && was != seen)
        {
            seen = was;
        }
    }
}
