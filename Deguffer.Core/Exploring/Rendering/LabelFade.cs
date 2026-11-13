using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// How a label's opacity follows the magnification of its picture along one axis, where one is the
/// magnification it was drawn at: none at or below <paramref name="Gone"/>, all of it at or above
/// <paramref name="Whole"/>, and in proportion between. <paramref name="Whole"/> is never above
/// <see cref="ExploreLabel.AtRest"/>, so a label is whole where its drawing put it, and always above
/// <paramref name="Gone"/>.
/// </summary>
public readonly record struct LabelFade(float Gone, float Whole)
{
    /// <summary>
    /// How many steps of <see cref="Step"/> there are to each halving of the magnification.
    ///
    /// <para>A map fades its names by step rather than one by one, so that every name whose fades fall in
    /// the same step on each axis shares one opacity the compositor works out, and the number of those
    /// opacities does not grow with the number of names. A step is under 4.5% of the magnification,
    /// against the fifth of it a fade runs over (<see cref="ExploreLabel.FadeRange"/>), so a name fading
    /// a step early is not seen to.</para>
    /// </summary>
    public const int StepsPerOctave = 16;

    /// <summary>
    /// The latest a stepped fade can start: just under <see cref="ExploreLabel.AtRest"/>, so a label at
    /// the top step still fades over a range, and is still whole at rest.
    /// </summary>
    private const float LatestGone = ExploreLabel.AtRest - (1f / 1024);

    /// <summary>
    /// The step of the magnification this fade starts at, rounded so the stepped fade
    /// (<see cref="AtStep"/>) is gone no later than this one: a name whose fade is moved to its step is
    /// never left over a neighbour's shape. Only a fade starting within a 1,024th of
    /// <see cref="ExploreLabel.AtRest"/> starts later, at <see cref="LatestGone"/>.
    /// </summary>
    public int Step
    {
        get
        {
            // Less a sliver, so a fade that starts exactly on a step is that step and not the next,
            // whatever the rounding of the logarithm.
            var step = (int)Math.Ceiling((Math.Log2(Gone) * StepsPerOctave) - (1.0 / 1024));

            return Math.Min(step, 0);
        }
    }

    /// <summary>The fade of every label whose fade is at <paramref name="step"/> (see <see cref="Step"/>).</summary>
    public static LabelFade AtStep(int step)
    {
        var gone = Math.Min((float)Math.Pow(2, (double)step / StepsPerOctave), LatestGone);

        return new LabelFade(gone, Math.Min(ExploreLabel.AtRest, gone * ExploreLabel.FadeRange));
    }

    /// <summary>
    /// How opaque a label is with its picture magnified <paramref name="across"/> and
    /// <paramref name="down"/>, for its fades <paramref name="fadeAcross"/> and
    /// <paramref name="fadeDown"/>: the less opaque of the two, so a wide shape does not keep a name in
    /// a band that has shrunk. Worked over <see cref="IScalar{T}"/>, so the opacity the compositor
    /// works out on every frame is this rule.
    /// </summary>
    public static T Opacity<T>(LabelFade fadeAcross, LabelFade fadeDown, T across, T down)
        where T : IScalar<T> =>
        T.Clamp(T.Min(fadeAcross.Ramp(across), fadeDown.Ramp(down)), T.Of(0), T.Of(1));

    /// <summary>How far through this fade <paramref name="magnification"/> is: zero where gone, one where whole, and beyond each outside them.</summary>
    private T Ramp<T>(T magnification)
        where T : IScalar<T> =>
        (magnification - T.Of(Gone)) / T.Of(Whole - Gone);
}
