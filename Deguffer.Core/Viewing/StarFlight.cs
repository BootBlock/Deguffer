namespace Deguffer.Core.Viewing;

/// <summary>
/// Where the camera is, a moment into its flight through the About page's starfield.
/// </summary>
/// <param name="Bank">The roll about the line of flight, in radians.</param>
/// <param name="Yaw">The turn to either side, in radians.</param>
/// <param name="Pitch">The turn up or down, in radians.</param>
/// <param name="Travel">How far the camera has flown since the loop began, in device-independent pixels.</param>
/// <param name="Streak">How long a star is drawn, as a multiple of its length at rest.</param>
public readonly record struct StarPose(float Bank, float Yaw, float Pitch, float Travel, float Streak);

/// <summary>
/// The camera's flight through the starfield: a slow loop that banks, turns and climbs, and flies
/// faster and slower as it goes.
///
/// <para>Every turn is a whole number of waves over the loop and the speed's own wave is too, so the
/// pose the loop ends on is the pose it began on, and the distance flown is a whole number of the
/// field's depths. Each star is placed by that distance modulo the depth, so the stars stand where
/// they stood at the start as well, and the loop has no seam to see. The compositor clocks the loop,
/// from <see cref="Samples"/>, so the flight costs the UI thread nothing.</para>
/// </summary>
public sealed class StarFlight
{
    /// <summary>How far the camera banks either way at most: enough to read as flight, not a spin.</summary>
    public const float MaxBank = 18 * MathF.PI / 180;

    /// <summary>How far it turns to either side at most.</summary>
    public const float MaxYaw = 8 * MathF.PI / 180;

    /// <summary>How far it turns up or down at most.</summary>
    public const float MaxPitch = 5 * MathF.PI / 180;

    /// <summary>
    /// How far the speed swings either side of its mean, as a share of the mean. Under 1, so the camera
    /// always flies forward: a star that turned and flew back into the depth would read as a fault.
    /// </summary>
    public const float SpeedSwing = 0.5f;

    /// <summary>How much longer a star is drawn at the mean speed than at rest.</summary>
    public const float StreakAtMeanSpeed = 4;

    /// <summary>
    /// A flight whose loop takes <paramref name="period"/> through a field <paramref name="depth"/>
    /// deep, at a mean speed as near to <paramref name="speed"/> as a seamless loop allows.
    /// </summary>
    public StarFlight(TimeSpan period, float depth, float speed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(speed);

        Period = period;
        Depth = depth;

        // The loop flies a whole number of depths, never none, so the mean speed is the nearest to the
        // one asked for that comes out whole.
        var depths = Math.Max(1, MathF.Round(speed * (float)period.TotalSeconds / depth));
        LoopDistance = depths * depth;
        MeanSpeed = LoopDistance / (float)period.TotalSeconds;
    }

    /// <summary>How long one loop of the flight takes.</summary>
    public TimeSpan Period { get; }

    /// <summary>How deep the field is, front to back, in device-independent pixels.</summary>
    public float Depth { get; }

    /// <summary>How far one loop flies: a whole number of <see cref="Depth"/>s.</summary>
    public float LoopDistance { get; }

    /// <summary>The camera's speed averaged over the loop, in device-independent pixels a second.</summary>
    public float MeanSpeed { get; }

    /// <summary>
    /// The still field a reader with animation effects off is shown: the loop's first pose, with the
    /// stars at rest, because a streak says the camera is moving and it is not.
    /// </summary>
    public StarPose Rest => At(TimeSpan.Zero) with { Streak = 1 };

    /// <summary>The pose <paramref name="time"/> into the flight, which loops every <see cref="Period"/>.</summary>
    public StarPose At(TimeSpan time)
    {
        var period = (float)Period.TotalSeconds;
        var seconds = (float)(time.TotalSeconds % Period.TotalSeconds);
        var turn = 2 * MathF.PI * seconds / period;

        // The speed is the mean with a sine wave of two cycles over the loop on it, and the distance is
        // its integral: the wave's share integrates to nothing over whole cycles, which is what makes
        // the loop's distance exactly the mean speed times the period.
        var speed = MeanSpeed * (1 + (SpeedSwing * MathF.Sin(2 * turn)));
        var travel = MeanSpeed * (seconds + (SpeedSwing * period / (4 * MathF.PI) * (1 - MathF.Cos(2 * turn))));

        return new StarPose(
            Bank: MaxBank * MathF.Sin(turn),
            Yaw: MaxYaw * MathF.Sin((2 * turn) + (MathF.PI / 3)),
            Pitch: MaxPitch * MathF.Sin(3 * turn),
            Travel: travel,
            Streak: 1 + (StreakAtMeanSpeed * speed / MeanSpeed));
    }

    /// <summary>
    /// The loop as <paramref name="steps"/> equal steps, for the compositor to play as key frames: one
    /// more pose than steps, the last at the end of the loop. That last pose is the first again, except
    /// that it has flown <see cref="LoopDistance"/>, which the field shows as no change at all.
    /// </summary>
    public IReadOnlyList<StarPose> Samples(int steps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(steps);

        var poses = new StarPose[steps + 1];

        for (var step = 0; step < steps; step++)
        {
            poses[step] = At(Period * step / steps);
        }

        // Written rather than computed, because At wraps its time back into the loop and would give the
        // loop's first distance, not its last.
        poses[steps] = poses[0] with { Travel = poses[0].Travel + LoopDistance };

        return poses;
    }
}
