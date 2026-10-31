using Deguffer.Core.Exploring.Layout;
using Microsoft.UI.Composition;
using Windows.Graphics;

namespace Deguffer.App.Controls;

/// <summary>
/// A small copy of the latest whole drawing of a picture to arrive, for the map's overview, kept until
/// another replaces it or the picture is of another place.
///
/// <para>A copy rather than the whole drawing's own surface, because that surface is taken for another
/// drawing as soon as the picture changes, and the overview is of the same place in the old look until
/// the new whole drawing arrives. Kept with the drawings of the picture (<see cref="ExploreLayers"/>)
/// rather than by the overview, so a change of folder turned round brings back the overview of the
/// folder it returns to with its drawings.</para>
/// </summary>
/// <param name="changed">Told each time <see cref="Surface"/> has a new answer.</param>
internal sealed class ExploreMiniature(MapGraphics graphics, Action changed)
{
    /// <summary>The copy, written again for each whole drawing rather than made again (G5).</summary>
    private CompositionDrawingSurface? _surface;

    private bool _isCurrent;

    /// <summary>The copy, or null while none has arrived of the place on screen.</summary>
    public ICompositionSurface? Surface => _isCurrent ? _surface : null;

    /// <summary>
    /// Copy a whole drawing <paramref name="width"/> by <paramref name="height"/> that has just arrived,
    /// small: at the overview's size on a screen <paramref name="screenWidth"/> by
    /// <paramref name="screenHeight"/>, in the drawing's own pixels to each device-independent one. A
    /// map too small for an overview keeps none.
    /// </summary>
    /// <param name="pixels">The drawing as painted on the CPU, or null for one just shaded on the GPU.</param>
    public void Copy(byte[]? pixels, int width, int height, double screenWidth, double screenHeight)
    {
        if (MapOverview.Size(screenWidth, screenHeight) is not { } shown)
        {
            Forget();
            return;
        }

        var size = new SizeInt32(
            Math.Max(1, (int)Math.Round(width * shown.Width / screenWidth)),
            Math.Max(1, (int)Math.Round(height * shown.Height / screenHeight)));

        var shrunk = pixels is null
            ? graphics.ShrinkShaded(_surface, width, height, size)
            : graphics.Shrink(_surface, pixels, width, height, size);

        if (shrunk is not { } surface)
        {
            return;
        }

        _surface = surface;
        _isCurrent = true;
        changed();
    }

    /// <summary>
    /// The picture is of another place now: another tree, folder or view. The copy of the old one goes
    /// at once, rather than standing for the new one until its whole drawing arrives.
    /// </summary>
    public void Forget()
    {
        if (!_isCurrent)
        {
            return;
        }

        _isCurrent = false;
        changed();
    }

    /// <summary>
    /// Let go of the copy's surface, for a device that has replaced the one it was written through:
    /// made before the replacement, it would take a write and show nothing (see
    /// <see cref="CompositionDevice.Replaced"/>).
    /// </summary>
    public void Renew()
    {
        _surface = null;
        Forget();
    }
}
