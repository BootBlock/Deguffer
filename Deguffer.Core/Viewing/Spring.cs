namespace Deguffer.Core.Viewing;

/// <summary>
/// A progress from 0 to 1, or back, pulled toward where it is going by a critically damped spring:
/// it sets off at whatever speed it already had, and comes to rest without passing where it was going
/// and swinging back.
///
/// <para>A spring rather than an eased curve for a move that can be turned round part of the way: an
/// eased curve starts again from a standstill, so a move asked to go back the way it came would stop
/// dead and set off again, where a spring carries its speed into the new move and nothing jumps. The
/// arithmetic is the closed form, so where the spring is at any moment is known exactly on whatever
/// clock the caller keeps, however many frames the display dropped.</para>
///
/// <para>Critically damped because what it moves fills the screen at one end: a spring that passed its
/// end would show the screen's edge bare for a moment before it came back.</para>
/// </summary>
public readonly record struct Spring
{
    /// <summary>How near its end, as a fraction of the whole move, the spring counts as having arrived.</summary>
    private const double Near = 1e-3;

    /// <summary>
    /// The rate, in units of one over the motion's duration, at which a move from a standstill across
    /// the whole of its range is within <see cref="Near"/> of its end as the duration runs out: within
    /// nine tenths of it, so it has arrived by then whatever the rounding.
    /// </summary>
    private static readonly double SettlingRate = SolveSettlingRate();

    private Spring(double target, double offset, double speed, TimeSpan start, double rate)
    {
        Target = target;
        _offset = offset;
        _speed = speed;
        _start = start;
        _rate = rate;
    }

    /// <summary>How far from <see cref="Target"/> it was at <see cref="_start"/>.</summary>
    private readonly double _offset;

    /// <summary>How fast it was moving at <see cref="_start"/>, in progress a second.</summary>
    private readonly double _speed;

    private readonly TimeSpan _start;

    /// <summary>How stiff the spring is, in one over a second. Zero for one that is already where it is going.</summary>
    private readonly double _rate;

    /// <summary>Where it is going.</summary>
    public double Target { get; }

    /// <summary>At rest at <paramref name="progress"/>.</summary>
    public static Spring At(double progress) => new(progress, 0, 0, TimeSpan.Zero, 0);

    /// <summary>
    /// From wherever this is at <paramref name="now"/>, and at the speed it has there, toward
    /// <paramref name="target"/>, settling as <paramref name="motion"/> says: within its duration for a
    /// move across the whole range from a standstill. A motion with no duration is there at once.
    /// </summary>
    public Spring Toward(double target, TimeSpan now, Motion motion)
    {
        if (motion.IsInstant)
        {
            return At(target);
        }

        return new Spring(
            target,
            ValueAt(now) - target,
            SpeedAt(now),
            now,
            SettlingRate / motion.Duration.TotalSeconds);
    }

    /// <summary>Where it is at <paramref name="now"/>.</summary>
    public double ValueAt(TimeSpan now)
    {
        var (t, decay) = Since(now);

        return Target + ((_offset + (Lead * t)) * decay);
    }

    /// <summary>How fast it is moving at <paramref name="now"/>, in progress a second.</summary>
    public double SpeedAt(TimeSpan now)
    {
        var (t, decay) = Since(now);

        return (_speed - (_rate * Lead * t)) * decay;
    }

    /// <summary>
    /// Whether it has arrived by <paramref name="now"/>: within <see cref="Near"/> of where it is going,
    /// and too slow to leave it again.
    /// </summary>
    public bool IsOverAt(TimeSpan now) =>
        _rate == 0
        || (Math.Abs(ValueAt(now) - Target) <= Near && Math.Abs(SpeedAt(now)) <= Near * _rate);

    /// <summary>The second term of the closed form, x(t) = target + (offset + lead·t)·e^(−rate·t).</summary>
    private double Lead => _speed + (_rate * _offset);

    private (double Seconds, double Decay) Since(TimeSpan now)
    {
        var t = Math.Max(0, (now - _start).TotalSeconds);

        return (t, Math.Exp(-_rate * t));
    }

    /// <summary>
    /// The x for which (1 + x)·e^(−x) is nine tenths of <see cref="Near"/>: how far a critically damped spring let go at
    /// a standstill one unit from its end is from it after x over its rate. Newton's method from below
    /// the root: past x = 1 the function falls and is convex, so each step lands nearer the root
    /// without passing it.
    /// </summary>
    private static double SolveSettlingRate()
    {
        var x = 1.0;

        for (var i = 0; i < 50; i++)
        {
            var value = ((1 + x) * Math.Exp(-x)) - (Near * 0.9);
            var slope = -x * Math.Exp(-x);

            x -= value / slope;
        }

        return x;
    }
}
