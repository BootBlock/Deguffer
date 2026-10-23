using System.Numerics;
using System.Runtime.InteropServices;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.App.Controls;

/// <summary>
/// What <c>Cushions.hlsl</c> reads for one draw, as its constant buffer holds it. The shader's
/// <c>cbuffer</c> is declared from this type (see <see cref="ConstantBuffer"/>), so a field added,
/// removed or moved here moves there too.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct CushionConstants
{
    /// <summary>The canvas's size in pixels, which is where the viewport puts it.</summary>
    public readonly Vector2 Size;

    /// <summary>The canvas point under the pointer's light.</summary>
    public readonly Vector2 LightAt;

    /// <summary>The part of the canvas shaded: left, top, right, bottom. Nothing outside it is touched.</summary>
    public readonly Vector4 Region;

    /// <summary>How far the pointer's light reaches, in pixels.</summary>
    public readonly float Reach;

    /// <summary>How much the pointer's light adds facing it straight on: zero for no light.</summary>
    public readonly float Strength;

    /// <summary>How high above the canvas the pointer's light is, in pixels.</summary>
    public readonly float Height;

    /// <summary>How many pixels wide the line inside each quad's sides is drawn: zero for none.</summary>
    public readonly float LineWidth;

    /// <summary>The line's tone: red, green and blue from 0 to 255.</summary>
    public readonly Vector3 Tone;

    /// <summary>The shading, as <see cref="Core.Configuration.MapShading"/> numbers it.</summary>
    public readonly uint Shading;

    /// <summary>How far a block's line is mixed towards the tone, out of <see cref="ShapeFinish.Whole"/>.</summary>
    public readonly uint BlockLine;

    /// <summary>How far a folder's line is mixed towards the tone, out of <see cref="ShapeFinish.Whole"/>.</summary>
    public readonly uint FolderLine;

    /// <summary>How many pixels wide the bevel is.</summary>
    public readonly float BevelWidth;

    /// <summary>How far the bevel's lit side is mixed towards white, out of <see cref="ShapeFinish.Whole"/>.</summary>
    public readonly uint BevelHighlight;

    /// <summary>How far the bevel's shaded side is mixed towards black, out of <see cref="ShapeFinish.Whole"/>.</summary>
    public readonly uint BevelShadow;

    /// <summary>How far a sheen's light falls from a quad's top row to its middle (<see cref="GradientShading.SlopeAt"/>).</summary>
    public readonly uint SheenSlope;

    /// <summary>
    /// The constants for <paramref name="region"/> of a canvas <paramref name="width"/> by
    /// <paramref name="height"/>, finished as <paramref name="finish"/> says, under
    /// <paramref name="light"/> where there is one.
    /// </summary>
    public CushionConstants(int width, int height, CanvasRegion region, PointerLight? light, ShapeFinish finish)
    {
        var shining = light ?? default;

        Size = new Vector2(width, height);
        LightAt = new Vector2(shining.X, shining.Y);
        Region = new Vector4(region.X, region.Y, region.Right, region.Bottom);
        Reach = shining.Reach;
        Strength = light is null ? 0 : shining.Strength;
        Height = shining.Reach * PointerLight.Height;
        LineWidth = finish.Borders.Width;
        Tone = new Vector3(finish.Borders.Tone.Red, finish.Borders.Tone.Green, finish.Borders.Tone.Blue);
        Shading = (uint)finish.Shading;
        BlockLine = (uint)finish.Borders.Blocks;
        FolderLine = (uint)finish.Borders.Folders;
        BevelWidth = finish.Bevel.Width;
        BevelHighlight = (uint)finish.Bevel.Highlight;
        BevelShadow = (uint)finish.Bevel.Shadow;
        SheenSlope = (uint)GradientShading.SlopeAt(finish.Relief);
    }
}
