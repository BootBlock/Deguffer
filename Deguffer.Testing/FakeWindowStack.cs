using Deguffer.Core.Viewing;

namespace Deguffer.Testing;

/// <summary>
/// The top-level windows and monitors a test says the desktop holds, front to back in the order they
/// are added. This machine's own desktop cannot be made to hold a see-through window in front of
/// Deguffer's, a cloaked one, or a monitor layout of a test's choosing.
/// </summary>
internal sealed class FakeWindowStack : IWindowStack
{
    private readonly List<FakeStackedWindow> _windows = [];

    /// <summary>False where Windows refuses to enumerate the windows at all.</summary>
    public bool Enumerates { get; set; } = true;

    /// <summary>The monitors, or null where Windows refuses to list them.</summary>
    public IReadOnlyList<ScreenRect>? Monitors { get; set; } = [new ScreenRect(0, 0, 1920, 1080)];

    /// <summary>How many windows' facts were read, which shows a hidden answer stops reading early.</summary>
    public int Reads { get; private set; }

    public FakeWindowStack Add(FakeStackedWindow window)
    {
        _windows.Add(window);
        return this;
    }

    public IReadOnlyList<nint>? FrontToBack() => Enumerates ? _windows.ConvertAll(window => window.Handle) : null;

    public IReadOnlyList<ScreenRect>? Screens() => Monitors;

    public ScreenRect? Frame(nint window) => Find(window).Frame;

    public bool IsVisible(nint window)
    {
        Reads++;
        return Find(window).Visible;
    }

    public bool IsMinimised(nint window) => Find(window).Minimised;

    public bool? IsCloaked(nint window) => Find(window).Cloaked;

    public bool IsSolid(nint window) => Find(window).Solid;

    /// <summary>A handle the desktop does not hold answers as Windows answers for a window that has gone.</summary>
    private FakeStackedWindow Find(nint window) => _windows.Find(candidate => candidate.Handle == window)
        ?? new FakeStackedWindow(window, Frame: null) { Visible = false, Cloaked = null, Solid = false };
}

/// <summary>One top-level window a test puts on a <see cref="FakeWindowStack"/>.</summary>
internal sealed record FakeStackedWindow(nint Handle, ScreenRect? Frame)
{
    public bool Visible { get; init; } = true;

    public bool Minimised { get; init; }

    public bool? Cloaked { get; init; } = false;

    public bool Solid { get; init; } = true;
}
