using Deguffer.Core.Configuration;
using Microsoft.Graphics.Canvas.Text;

namespace Deguffer.App.Controls;

/// <summary>
/// The type the map's names are set in, in either lettering (see <see cref="MapLettering"/>), shared
/// by every name on one map (see <see cref="NameText"/>).
///
/// <para>A format is the same for every name of one spacing or one text size, and a map has one of
/// each for each text size it is drawn at, so one made for each name on each drawing would be a
/// DirectWrite object per name per drawing for no difference. A format is never changed once made,
/// because every name shares it: what differs between names, such as alignment, is set on the name's
/// layout.</para>
/// </summary>
internal sealed class NameFormats
{
    /// <summary>SpaceMonger's type: its own bitmap font is one DirectWrite cannot draw, and this is the outline font Windows ships in its place.</summary>
    private const string ClassicFont = "Microsoft Sans Serif";

    /// <summary>SpaceMonger's 8 point type, in device-independent pixels.</summary>
    private const float ClassicSize = 11;

    /// <summary>How far down a line of SpaceMonger's type its baseline is, as a fraction of the line's height.</summary>
    private const float Baseline = 0.8f;

    /// <summary>
    /// The application's own type, as the window's text blocks set it: Windows 11's variable interface
    /// font at the optical size made for small text. DirectWrite falls back to the system's interface
    /// font where it is missing.
    /// </summary>
    private const string StandardFont = "Segoe UI Variable Text";

    /// <summary>The application's type at the standard text size, in device-independent pixels: the window's body text.</summary>
    private const float StandardSize = 12;

    private readonly Dictionary<float, CanvasTextFormat> _classic = [];

    private readonly Dictionary<double, CanvasTextFormat> _standard = [];

    /// <summary>SpaceMonger's type, in lines <paramref name="spacing"/> device-independent pixels apart.</summary>
    public CanvasTextFormat Classic(float spacing)
    {
        if (!_classic.TryGetValue(spacing, out var format))
        {
            format = new CanvasTextFormat
            {
                FontFamily = ClassicFont,
                FontSize = ClassicSize,
                WordWrapping = CanvasWordWrapping.NoWrap,
                LineSpacingMode = CanvasLineSpacingMode.Uniform,
                LineSpacing = spacing,
                LineSpacingBaseline = spacing * Baseline,
            };

            _classic.Add(spacing, format);
        }

        return format;
    }

    /// <summary>
    /// The application's type at the reader's text size, <paramref name="textScale"/> times the
    /// standard, on one line cut short with an ellipsis where it has too little room, as a text block's
    /// is.
    /// </summary>
    public CanvasTextFormat Standard(double textScale)
    {
        if (!_standard.TryGetValue(textScale, out var format))
        {
            format = new CanvasTextFormat
            {
                FontFamily = StandardFont,
                FontSize = (float)(StandardSize * textScale),
                WordWrapping = CanvasWordWrapping.NoWrap,
                TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
                TrimmingSign = CanvasTrimmingSign.Ellipsis,
            };

            _standard.Add(textScale, format);
        }

        return format;
    }
}
