using Deguffer.Core.Configuration;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;

namespace Deguffer.App.Controls;

/// <summary>
/// The text of one name on the map, set in the drawing's lettering (<see cref="MapLettering"/>): what
/// it says, how much room it takes, and drawing it.
///
/// <para>Drawn rather than set in a text block, so that a name is a rectangle of the one surface every
/// name is drawn into (<see cref="NameSheet"/>) and the compositor moves every name of a drawing with
/// one transform. SpaceMonger's lettering needs drawing anyway: its type is drawn without smoothing,
/// pixel for pixel, and a text block cannot be asked to draw without it. The application's own type is
/// drawn as a text block draws it over a picture: smoothed in grey, on one line, cut short with an
/// ellipsis where it has too little room.</para>
///
/// <para>Drawn again only when what it shows changes, which is noted here, or when where it was drawn
/// is lost, which its sprite says (<see cref="Forget"/>). The text is measured again only when it or
/// its type changes, which is what its natural width depends on: a name written again unchanged, as
/// most are from one drawing to the next, costs a comparison.</para>
/// </summary>
internal sealed class NameText
{
    /// <summary>The text last measured, in its type, and how wide and how high it is with room to spare, in device-independent pixels.</summary>
    private (string Text, CanvasTextFormat Format, float Width, float Height)? _measured;

    /// <summary>What the name is to show, as last written.</summary>
    private Drawing? _wanted;

    /// <summary>What it showed when it was last drawn, or nothing where that is lost.</summary>
    private Drawing? _drawn;

    /// <summary>How much room the text takes, in device-independent pixels, as last written.</summary>
    public Size Size { get; private set; }

    /// <summary>Whether what it shows has been drawn since it was last written, and not lost since.</summary>
    public bool IsDrawn => _drawn is not null && _drawn == _wanted;

    /// <summary>
    /// Write what <paramref name="writing"/> says, measured on <paramref name="device"/> in one of
    /// <paramref name="formats"/>: how much room it takes is <see cref="Size"/>.
    /// </summary>
    public void Write(CanvasDevice device, NameFormats formats, in NameWriting writing)
    {
        var classic = writing.Lettering == MapLettering.Classic;
        var format = classic ? formats.Classic(writing.Spacing) : formats.Standard(writing.TextScale);
        var (natural, height) = Measure(device, writing.Text, format, classic);

        if (classic)
        {
            var lines = writing.Text.Length == 0 ? 0 : writing.Text.AsSpan().Count('\n') + 1;
            height = lines * writing.Spacing;
        }

        // Centred in its box where it fits; where it does not, from the box's left, cut off at its
        // right, as SpaceMonger writes a name too long for its box and a text block trims one.
        var fits = natural <= writing.Room;
        var width = Math.Max(1, fits ? natural : writing.Room);

        height = Math.Max(1, height);
        _wanted = new Drawing(writing.Text, format, classic, width, height, writing.Centred && fits, writing.Colour);
        Size = new Size(width, height);
    }

    /// <summary>Draw what the name shows into <paramref name="session"/>, which is over the name's own rectangle and cleared.</summary>
    public void Draw(CanvasDrawingSession session)
    {
        if (_wanted is not { } wanted)
        {
            return;
        }

        using var layout = new CanvasTextLayout(session, wanted.Text, wanted.Format, wanted.Width, wanted.Height)
        {
            HorizontalAlignment = wanted.Centred ? CanvasHorizontalAlignment.Center : CanvasHorizontalAlignment.Left,
        };

        session.TextAntialiasing = wanted.Classic ? CanvasTextAntialiasing.Aliased : CanvasTextAntialiasing.Grayscale;

        // Kept to its box: a name cut short runs on past its right edge, into the name beside it on
        // the sheet, and a drawing session over part of a surface does not stop it there.
        using (session.CreateLayer(1, new Rect(0, 0, wanted.Width, wanted.Height)))
        {
            session.DrawTextLayout(layout, 0, 0, wanted.Colour);
        }

        _drawn = wanted;
    }

    /// <summary>Where the name was drawn holds it no longer: a new rectangle, or a sheet emptied or made again.</summary>
    public void Forget() => _drawn = null;

    /// <summary>
    /// How wide and how high <paramref name="text"/> is in <paramref name="format"/> with no box to fit,
    /// as it was last measured where neither has changed. SpaceMonger's type is measured to whole
    /// pixels, as it is drawn.
    /// </summary>
    private (float Width, float Height) Measure(CanvasDevice device, string text, CanvasTextFormat format, bool classic)
    {
        if (_measured is { } measured && ReferenceEquals(measured.Format, format) && string.Equals(measured.Text, text, StringComparison.Ordinal))
        {
            return (measured.Width, measured.Height);
        }

        using var measuring = new CanvasTextLayout(device, text, format, float.MaxValue, float.MaxValue);
        var bounds = measuring.LayoutBounds;
        var width = classic ? (float)Math.Ceiling(bounds.Width) : (float)bounds.Width;

        _measured = (text, format, width, (float)bounds.Height);

        return (width, (float)bounds.Height);
    }

    /// <summary>What a name shows: its text in its type, its box, how it sits in the box, and its colour.</summary>
    private readonly record struct Drawing(
        string Text,
        CanvasTextFormat Format,
        bool Classic,
        float Width,
        float Height,
        bool Centred,
        Color Colour);
}

/// <summary>
/// What one name of a drawing says and how (see <see cref="NameText.Write"/>): its text in its
/// lettering, in a box <paramref name="Room"/> device-independent pixels wide, centred in it where
/// <paramref name="Centred"/> and it fits, in <paramref name="Colour"/>. Lines of SpaceMonger's type
/// are <paramref name="Spacing"/> apart, and the application's type is set at
/// <paramref name="TextScale"/> times its standard size.
/// </summary>
internal readonly record struct NameWriting(
    string Text,
    MapLettering Lettering,
    float Room,
    bool Centred,
    Color Colour,
    float Spacing,
    double TextScale);
