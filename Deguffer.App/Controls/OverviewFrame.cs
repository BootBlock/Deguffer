using Deguffer.Core.Exploring.Layout;

namespace Deguffer.App.Controls;

/// <summary>
/// The overview's rectangle round the part of the picture on the screen, as the compositor works it
/// out at every frame: by <see cref="MapOverview.FrameSpan{T}"/> and <see cref="MapOverview.FrameStart{T}"/>,
/// the rules <see cref="MapOverview.Frame"/> works it out by for a press (§7.1).
///
/// <para>Each expression reads the camera's properties as <c>camera</c> (<see cref="MapCamera.Properties"/>)
/// and the overview's as <c>overview</c>, which holds the picture's size in the overview as
/// <see cref="Picture"/>.</para>
/// </summary>
internal static class OverviewFrame
{
    /// <summary>The overview's property holding the picture's size in it, in device-independent pixels.</summary>
    public const string Picture = "Size";

    private static readonly ExpressionScalar Width = new($"overview.{Picture}.X");

    private static readonly ExpressionScalar Height = new($"overview.{Picture}.Y");

    private static readonly ExpressionScalar ZoomX = new("camera.Scale.X");

    private static readonly ExpressionScalar ZoomY = new("camera.Scale.Y");

    private static readonly ExpressionScalar Across = MapOverview.FrameSpan(ZoomX, Width);

    private static readonly ExpressionScalar Down = MapOverview.FrameSpan(ZoomY, Height);

    /// <summary>The rectangle's top-left corner in the overview, a <c>Vector2</c>.</summary>
    public static string Offset { get; } =
        $"Vector2({MapOverview.FrameStart(MapCamera.ShownEdge("X"), ZoomX, Across) * Width}, "
            + $"{MapOverview.FrameStart(MapCamera.ShownEdge("Y"), ZoomY, Down) * Height})";

    /// <summary>The rectangle's size in the overview, a <c>Vector2</c>.</summary>
    public static string Size { get; } = $"Vector2({Across * Width}, {Down * Height})";
}
