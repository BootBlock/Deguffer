using System.Numerics;
using Deguffer.App.Shell;
using Deguffer.Core.Viewing;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Deguffer.App.Controls;

/// <summary>
/// The About page's starfield: a few thousand stars the camera flies through on a slow loop that banks
/// and turns, in the spirit of a 1990s demo. Stars come out of the depth, brighten as they near and
/// streak as the camera speeds up.
///
/// <para>Composition visuals under a perspective transform, with no drawing surface at all, so a lost
/// graphics device costs it nothing, and nothing per frame on the UI thread: the flight
/// (<see cref="StarFlight"/>) is key frames the compositor plays, and every star is placed by
/// expressions on them. The stars stand on sheets (<see cref="StarSky"/>), each sheet flown through as
/// one, so the compositor moves a few dozen visuals, not thousands. Each star is a thin card lying along
/// the line of flight, so perspective draws it as a streak pointing out of the vanishing point, and a
/// sheet stretched along that line lengthens every streak on it at once.</para>
///
/// <para>It is decoration: it takes no input and assistive technology skips it. What it does is
/// <see cref="StarfieldModes"/>' decision, asked again whenever an answer that feeds it changes: the
/// page coming or going, the window minimised, restored, hidden or shown, high contrast, the
/// compositor's word on effects, and the reader's animation setting.</para>
/// </summary>
public sealed partial class Starfield : Grid
{
    private const int StarCount = 3000;
    private const int Sheets = 80;

    /// <summary>The same sky on every visit.</summary>
    private const int Seed = 1990;

    /// <summary>How far from the line of flight the nearest star is, so none comes straight at the camera.</summary>
    private const float Hole = 60;

    /// <summary>How far from the line of flight the furthest star is: past the corners of a large window.</summary>
    private const float Radius = 1600;

    /// <summary>How deep the field is, front to back.</summary>
    private const float Depth = 3000;

    /// <summary>How far in front of the screen the camera's eye is, which sets how strong the perspective is.</summary>
    private const float Eye = 600;

    /// <summary>
    /// How far in front of the screen a star gets before it goes round to the back again. Short of the
    /// eye, because a star at the eye would be drawn infinitely large, and one past it upside down.
    /// </summary>
    private const float Near = 480;

    /// <summary>How far before it goes round a star has faded out, so it leaves rather than vanishes.</summary>
    private const float FadeOut = 600;

    /// <summary>How far a star comes out of the back before it is at its full light, so none pops in.</summary>
    private const float FadeIn = 300;

    /// <summary>How bright a star is at the back, as a share of its light at the front.</summary>
    private const float FarLight = 0.3f;

    /// <summary>The cruising speed asked of the flight, in device-independent pixels a second.</summary>
    private const float Speed = 600;

    /// <summary>A star at rest: its length along the line of flight, and its width across it, a round dot.</summary>
    private const float StarLength = 3;
    private const float StarWidth = 3;

    /// <summary>The steps the loop is played in: a quarter of a second each, too short to see a corner in.</summary>
    private const int Steps = 160;

    private static readonly UISettings SystemSettings = new();

    private static readonly StarFlight Flight = new(MotionToken.Starfield.Full.Duration, Depth, Speed);

    private readonly Compositor _compositor;
    private readonly ContainerVisual _root;
    private readonly CompositionPropertySet _camera;
    private readonly CompositionColorGradientStop _core;
    private readonly CompositionColorGradientStop _rim;
    private readonly AnimationController _flight;
    private readonly CompositionCapabilities _capabilities = new();

    private StarfieldMode _mode = StarfieldMode.Still;
    private bool _flown;

    public Starfield()
    {
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);

        _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _root = _compositor.CreateContainerVisual();
        _root.Clip = _compositor.CreateInsetClip();

        _camera = _compositor.CreatePropertySet();
        _camera.InsertVector2("Centre", Vector2.Zero);
        Pose(Flight.Rest);

        _flight = _compositor.CreateAnimationController();

        // One soft round dot for every star, so a theme change recolours the whole field in two writes.
        var dot = _compositor.CreateRadialGradientBrush();
        _core = _compositor.CreateColorGradientStop(0, Colors.White);
        _rim = _compositor.CreateColorGradientStop(1, Colors.Transparent);
        dot.ColorStops.Add(_core);
        dot.ColorStops.Add(_rim);

        _root.Children.InsertAtTop(World(dot));
        ElementCompositionPreview.SetElementChildVisual(this, _root);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Frame();
        ActualThemeChanged += (_, _) => Tint();
    }

    /// <summary>
    /// The camera's world: every sheet of stars, turned about the eye as the camera banks and turns.
    /// Turned about the eye rather than the screen, so the line of flight always passes through the eye
    /// and no star is ever seen edge on.
    /// </summary>
    private ContainerVisual World(CompositionBrush dot)
    {
        var world = _compositor.CreateContainerVisual();

        var turn = _compositor.CreateExpressionAnimation(
            "Matrix4x4.CreateTranslation(Vector3(-p.Centre.X, -p.Centre.Y, -Eye))"
            + " * Matrix4x4.CreateFromAxisAngle(Vector3(0, 0, 1), p.Bank)"
            + " * Matrix4x4.CreateFromAxisAngle(Vector3(0, 1, 0), p.Yaw)"
            + " * Matrix4x4.CreateFromAxisAngle(Vector3(1, 0, 0), p.Pitch)"
            + " * Matrix4x4.CreateTranslation(Vector3(p.Centre.X, p.Centre.Y, Eye))");
        turn.SetReferenceParameter("p", _camera);
        turn.SetScalarParameter("Eye", Eye);
        world.StartAnimation(nameof(Visual.TransformMatrix), turn);

        // How far a sheet is through the depth, from 0 at the back to the depth at the front: its own
        // start, plus the distance flown, round the depth.
        const string Through = "Mod(Base + p.Travel, Depth)";

        var place = _compositor.CreateExpressionAnimation($"Vector3(p.Centre.X, p.Centre.Y, {Through} - Depth + Near)");
        var light = _compositor.CreateExpressionAnimation(
            $"Clamp({Through} / FadeIn, 0, 1) * (FarLight + ((1 - FarLight) * {Through} / Depth)) * Clamp((Depth - {Through}) / FadeOut, 0, 1)");
        var stretch = _compositor.CreateExpressionAnimation("Vector3(1, 1, p.Streak)");

        foreach (var animation in new[] { place, light, stretch })
        {
            animation.SetReferenceParameter("p", _camera);
            animation.SetScalarParameter("Depth", Depth);
            animation.SetScalarParameter("Near", Near);
            animation.SetScalarParameter("FadeOut", FadeOut);
            animation.SetScalarParameter("FadeIn", FadeIn);
            animation.SetScalarParameter("FarLight", FarLight);
        }

        var sheets = new ContainerVisual[Sheets];

        for (var i = 0; i < Sheets; i++)
        {
            var sheet = _compositor.CreateContainerVisual();

            // A parameter is taken when the animation starts, so one animation serves every sheet.
            place.SetScalarParameter("Base", i * Depth / Sheets);
            light.SetScalarParameter("Base", i * Depth / Sheets);
            sheet.StartAnimation(nameof(Visual.Offset), place);
            sheet.StartAnimation(nameof(Visual.Opacity), light);
            sheet.StartAnimation(nameof(Visual.Scale), stretch);

            world.Children.InsertAtTop(sheet);
            sheets[i] = sheet;
        }

        foreach (var star in StarSky.Scatter(StarCount, Sheets, Hole, Radius, Seed))
        {
            var sprite = _compositor.CreateSpriteVisual();
            sprite.Size = new Vector2(StarLength, StarWidth);
            sprite.Brush = dot;
            sprite.TransformMatrix = Lying(star);
            sheets[star.Sheet].Children.InsertAtTop(sprite);
        }

        return world;
    }

    /// <summary>
    /// A star's card laid along the line of flight at its place on its sheet: its length runs along the
    /// line and its width across, square on to the line out from the middle, so perspective draws it as
    /// a streak that points out of the vanishing point. Centred on its place, so a sheet stretched along
    /// the line lengthens it both ways rather than pushing it back.
    /// </summary>
    private static Matrix4x4 Lying(Star star)
    {
        var distance = MathF.Sqrt((star.X * star.X) + (star.Y * star.Y));
        var outward = new Vector3(star.X / distance, star.Y / distance, 0);
        var across = new Vector3(-outward.Y, outward.X, 0);
        var along = Vector3.UnitZ;
        var place = new Vector3(star.X, star.Y, 0) - (along * (StarLength / 2)) - (across * (StarWidth / 2));

        return new Matrix4x4(
            along.X, along.Y, along.Z, 0,
            across.X, across.Y, across.Z, 0,
            outward.X, outward.Y, outward.Z, 0,
            place.X, place.Y, place.Z, 1);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        XamlRoot.Changed += OnRootChanged;
        SystemSettings.ColorValuesChanged += OnSystemColoursChanged;
        _capabilities.Changed += OnCapabilitiesChanged;
        SystemMotion.Current.Changed += OnMotionChanged;

        Frame();
        Tint();
        Decide();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is { } root)
        {
            root.Changed -= OnRootChanged;
        }

        SystemSettings.ColorValuesChanged -= OnSystemColoursChanged;
        _capabilities.Changed -= OnCapabilitiesChanged;
        SystemMotion.Current.Changed -= OnMotionChanged;

        Decide();
    }

    /// <summary>The window was minimised, restored, hidden or shown, or moved between displays.</summary>
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Decide();

    /// <summary>High contrast turned on or off. Raised off the UI thread.</summary>
    private void OnSystemColoursChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(Decide);

    /// <summary>The compositor's word on whether effects are fast, which a remote session changes. Raised off the UI thread.</summary>
    private void OnCapabilitiesChanged(CompositionCapabilities sender, object args) =>
        DispatcherQueue.TryEnqueue(Decide);

    /// <summary>The reader turned animation effects on or off. Raised on the UI thread.</summary>
    private void OnMotionChanged(object? sender, EventArgs e) => Decide();

    /// <summary>Put the field in the mode it should be in now.</summary>
    private void Decide()
    {
        var seen = IsLoaded && XamlRoot is { IsHostVisible: true };

        Play(StarfieldModes.For(
            SystemMotion.Current.For(MotionToken.Starfield),
            HighContrast.IsEnabled(),
            _capabilities.AreEffectsFast(),
            seen));
    }

    private void Play(StarfieldMode mode)
    {
        if (mode == _mode)
        {
            return;
        }

        _mode = mode;
        _root.IsVisible = mode != StarfieldMode.Absent;

        switch (mode)
        {
            case StarfieldMode.Flying when _flown:
                _flight.Resume();
                break;

            case StarfieldMode.Flying:
                Fly();
                break;

            case StarfieldMode.Paused:
                _flight.Pause();
                break;

            // Absent and Still alike: nothing moves, and a field shown again later starts from rest.
            default:
                Rest();
                break;
        }
    }

    /// <summary>Start the loop, for ever, on the compositor.</summary>
    private void Fly()
    {
        var samples = Flight.Samples(Steps);

        Animate("Bank", samples, pose => pose.Bank);
        Animate("Yaw", samples, pose => pose.Yaw);
        Animate("Pitch", samples, pose => pose.Pitch);
        Animate("Travel", samples, pose => pose.Travel);
        Animate("Streak", samples, pose => pose.Streak);

        _flight.Resume();
        _flown = true;
    }

    /// <summary>One of the camera's values, played through the loop's poses, linearly between them.</summary>
    private void Animate(string name, IReadOnlyList<StarPose> samples, Func<StarPose, float> value)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        var linear = _compositor.CreateLinearEasingFunction();

        for (var i = 0; i < samples.Count; i++)
        {
            animation.InsertKeyFrame(i / (float)(samples.Count - 1), value(samples[i]), linear);
        }

        animation.Duration = Flight.Period;
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        _camera.StartAnimation(name, animation, _flight);
    }

    /// <summary>Stop the loop and put the camera at rest.</summary>
    private void Rest()
    {
        if (_flown)
        {
            foreach (var name in new[] { "Bank", "Yaw", "Pitch", "Travel", "Streak" })
            {
                _camera.StopAnimation(name);
            }

            _flown = false;
        }

        Pose(Flight.Rest);
    }

    private void Pose(StarPose pose)
    {
        _camera.InsertScalar("Bank", pose.Bank);
        _camera.InsertScalar("Yaw", pose.Yaw);
        _camera.InsertScalar("Pitch", pose.Pitch);
        _camera.InsertScalar("Travel", pose.Travel);
        _camera.InsertScalar("Streak", pose.Streak);
    }

    /// <summary>
    /// Fit the field to the control: the vanishing point at its middle, the perspective about it, and
    /// nothing drawn outside it.
    /// </summary>
    private void Frame()
    {
        var size = ActualSize;
        var middle = new Vector3(size / 2, 0);
        var perspective = Matrix4x4.Identity;
        perspective.M34 = -1 / Eye;

        _root.Size = size;
        _root.TransformMatrix = Matrix4x4.CreateTranslation(-middle) * perspective * Matrix4x4.CreateTranslation(middle);
        _camera.InsertVector2("Centre", size / 2);
    }

    /// <summary>
    /// Light stars on the dark theme and dark ones on the light, as the theme's primary text is, so the
    /// field sits in the page as its words do. Read from this control's own theme, which follows the
    /// app's choice of theme rather than the system's.
    /// </summary>
    private void Tint()
    {
        var star = ActualTheme == ElementTheme.Light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White;

        _core.Color = star;
        _rim.Color = Color.FromArgb(0, star.R, star.G, star.B);
    }
}
