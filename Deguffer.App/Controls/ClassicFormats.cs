using Deguffer.Core.Configuration;
using Microsoft.Graphics.Canvas.Text;

namespace Deguffer.App.Controls;

/// <summary>
/// The type SpaceMonger's lettering (<see cref="MapLettering.Classic"/>) is set in, one format per
/// line spacing, shared by every name on one map (see <see cref="ClassicName{TImage}"/>).
///
/// <para>A format is the same for every name of one spacing, and a map has one spacing for each
/// text size it is drawn at, so one made for each name on each drawing would be a DirectWrite object
/// per name per drawing for no difference. A format is never changed once made, because every name
/// shares it: what differs between names, such as alignment, is set on the name's layout.</para>
/// </summary>
internal sealed class ClassicFormats
{
    /// <summary>SpaceMonger's type: its own bitmap font is one DirectWrite cannot draw, and this is the outline font Windows ships in its place.</summary>
    private const string Font = "Microsoft Sans Serif";

    /// <summary>SpaceMonger's 8 point type, in device-independent pixels.</summary>
    private const float FontSize = 11;

    /// <summary>How far down a line of the type its baseline is, as a fraction of the line's height.</summary>
    private const float Baseline = 0.8f;

    private readonly Dictionary<float, CanvasTextFormat> _formats = [];

    /// <summary>The format for lines <paramref name="spacing"/> device-independent pixels apart.</summary>
    public CanvasTextFormat For(float spacing)
    {
        if (!_formats.TryGetValue(spacing, out var format))
        {
            format = new CanvasTextFormat
            {
                FontFamily = Font,
                FontSize = FontSize,
                WordWrapping = CanvasWordWrapping.NoWrap,
                LineSpacingMode = CanvasLineSpacingMode.Uniform,
                LineSpacing = spacing,
                LineSpacingBaseline = spacing * Baseline,
            };

            _formats.Add(spacing, format);
        }

        return format;
    }
}
