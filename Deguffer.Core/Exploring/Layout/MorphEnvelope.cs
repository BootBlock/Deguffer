namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// How visible the shapes of a change of view are over the move (<see cref="ViewMorph"/>), as key
/// frames of the share of the move gone and the shapes' opacity.
///
/// <para>A shape is flat and the drawings under it are shaded, so the shapes fade in over the view
/// left and out over the view arrived at rather than standing in for either at once: a cut to them
/// would flatten the picture as the move starts, and a cut from them would flash the shading on as it
/// lands.</para>
/// </summary>
public static class MorphEnvelope
{
    /// <summary>
    /// How far into the move the shapes are fully over the picture, and how far from its end they
    /// start to give way to it.
    /// </summary>
    public const float Over = 0.2f;

    /// <summary>The key frames, in order of the share of the move gone, from its start to its end.</summary>
    public static IReadOnlyList<(float Progress, float Opacity)> Steps { get; } =
        [(0, 0), (Over, 1), (1 - Over, 1), (1, 0)];
}
