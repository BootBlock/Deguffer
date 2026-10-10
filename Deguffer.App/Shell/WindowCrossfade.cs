using System.Numerics;
using Deguffer.App.Controls;
using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics;

namespace Deguffer.App.Shell;

/// <summary>
/// A change to how the whole window looks, played as a fade from a still of the window before it
/// into the window after it, rather than a repaint all at once.
///
/// <para>For a theme chosen in the app. A theme Windows changes under a window following the system
/// has already changed by the time anything here hears of it, so there is nothing left to take a
/// still of, and that one repaints as before.</para>
///
/// <para>The still covers the window's content and not the backdrop behind it, which is the
/// system's to draw. <see cref="WindowBackdrop"/> fades that on its own.</para>
/// </summary>
internal sealed class WindowCrossfade
{
    private readonly FrameworkElement _root;

    private readonly IMotionPolicy _motion;

    private readonly Visual _window;

    /// <summary>The still of the window as it was, over the window as it is, while it fades.</summary>
    private readonly SpriteVisual _still;

    /// <summary>
    /// Whether a still is being taken. A change asked for meanwhile is left to the one waiting on the
    /// still, which makes whatever change is wanted by the time it lands: made at once, it would be
    /// covered by a still of the window before it, and then fade back in.
    /// </summary>
    private bool _capturing;

    /// <summary>
    /// Whether a still is fading. A change asked for meanwhile is made as it stands, under the still,
    /// rather than taking a still of a window that is part of the way through a fade.
    /// </summary>
    private bool _fading;

    public WindowCrossfade(FrameworkElement root, IMotionPolicy motion)
    {
        _root = root;
        _motion = motion;
        _window = ElementCompositionPreview.GetElementVisual(root);

        _still = _window.Compositor.CreateSpriteVisual();
        _still.RelativeSizeAdjustment = Vector2.One;
        _still.IsVisible = false;
        ElementCompositionPreview.SetElementChildVisual(root, _still);
    }

    /// <summary>
    /// Make <paramref name="change"/>, fading the window from how it looked before it. A change reads
    /// what it is to make when it is made, because one asked for while a still is taken is made by the
    /// change already waiting.
    /// </summary>
    public async Task Change(Action change)
    {
        var played = _motion.For(MotionToken.Crossfade);
        var scale = _root.XamlRoot?.RasterizationScale ?? 1;
        var size = new SizeInt32(
            (int)Math.Ceiling(_root.ActualWidth * scale),
            (int)Math.Ceiling(_root.ActualHeight * scale));

        if (_capturing)
        {
            return;
        }

        // Nothing on screen yet to fade from, or a fade already under way: changed as it stands.
        if (played.IsInstant || size.Width <= 0 || size.Height <= 0 || _fading)
        {
            change();
            return;
        }

        _capturing = true;
        var still = await MapGraphics.For(_window.Compositor).Capture(_window, size);
        _capturing = false;

        if (still is null)
        {
            change();
            return;
        }

        var brush = _window.Compositor.CreateSurfaceBrush(still);
        brush.Stretch = CompositionStretch.Fill;
        _still.Brush = brush;
        _still.Opacity = 1;
        _still.IsVisible = true;

        change();

        var fade = _window.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0);
        fade.Duration = played.Duration;

        _fading = true;
        var fading = _window.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _still.StartAnimation(nameof(Visual.Opacity), fade);
        fading.End();

        fading.Completed += (_, _) =>
        {
            _fading = false;
            _still.IsVisible = false;
            _still.Brush = null;
            brush.Dispose();
            (still as IDisposable)?.Dispose();
        };
    }
}
