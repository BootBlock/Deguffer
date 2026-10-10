using Deguffer.Core.Viewing;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Deguffer.App.Shell;

/// <summary>
/// Applies the §6.5 Acrylic backdrop, and takes it away again when it would harm legibility.
///
/// Windows already drops the material under battery saver, with transparency effects off, and
/// over Remote Desktop — that fallback is the system's job, and the UI is built to read on the
/// solid colour it falls back to. The one case Windows will not handle for us is high contrast,
/// where translucency actively fights the user's stated requirement, so the backdrop is removed
/// outright.
///
/// <para>Without the material the window stands on its own ground, a solid layer under everything
/// in it, which is what lets the change fade rather than snap: the ground fades in over the material
/// before the material is taken away, and the material goes in under the ground before the ground
/// fades away from over it.</para>
/// </summary>
internal sealed class WindowBackdrop
{
    private readonly Window _window;
    private readonly Visual _ground;
    private readonly IMotionPolicy _motion;
    private readonly DesktopAcrylicBackdrop _acrylic = new();
    private bool? _applied;
    private bool _requested = true;

    /// <param name="ground">The solid layer under the window's content, shown wherever the material is not.</param>
    public WindowBackdrop(Window window, UIElement ground, IMotionPolicy motion)
    {
        _window = window;
        _ground = ElementCompositionPreview.GetElementVisual(ground);
        _motion = motion;

        // XAML raises a theme change when high contrast is switched on or off, which saves
        // pumping WM_SETTINGCHANGE ourselves. It also fires on every light/dark switch, hence
        // the no-op guard in Apply.
        if (window.Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => Apply();
        }
    }

    /// <summary>
    /// Whether the user wants the material. High contrast still overrides it: §6.5 makes the
    /// backdrop decoration, and no preference may reinstate translucency over a stated
    /// accessibility requirement.
    /// </summary>
    public bool IsRequested
    {
        get => _requested;
        set
        {
            _requested = value;
            Apply();
        }
    }

    public void Apply()
    {
        var wanted = _requested && !HighContrast.IsEnabled();
        if (_applied == wanted)
        {
            return;
        }

        // The first is applied before the window is shown, so there is nothing yet to fade from.
        var first = _applied is null;
        _applied = wanted;

        if (first)
        {
            _window.SystemBackdrop = wanted ? _acrylic : null;
            _ground.Opacity = wanted ? 0 : 1;
            return;
        }

        if (wanted)
        {
            _window.SystemBackdrop = _acrylic;
            FadeGround(0, null);
        }
        else
        {
            // Asked again when the fade lands, because the material may have been asked back for
            // while the ground was still on its way in.
            FadeGround(1, () =>
            {
                if (_applied == false)
                {
                    _window.SystemBackdrop = null;
                }
            });
        }
    }

    /// <summary>Fade the ground to <paramref name="opacity"/>, from wherever it is, then do <paramref name="landed"/>.</summary>
    private void FadeGround(float opacity, Action? landed)
    {
        var compositor = _ground.Compositor;

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, opacity);
        fade.Duration = _motion.For(MotionToken.Crossfade).Duration;

        var fading = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _ground.StartAnimation(nameof(Visual.Opacity), fade);
        fading.End();

        if (landed is not null)
        {
            fading.Completed += (_, _) => landed();
        }
    }
}
