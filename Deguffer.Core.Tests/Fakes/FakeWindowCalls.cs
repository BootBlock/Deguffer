using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// The top-level windows a test says the desktop holds. This machine's own desktop cannot be made to
/// hold a cloaked window, a console window owned by a program that is not a console program, or a
/// window destroyed between two calls.
/// </summary>
internal sealed class FakeWindowCalls : IWindowCalls
{
    private readonly List<FakeWindow> _windows = [];

    /// <summary>False where Windows refuses to enumerate them at all.</summary>
    public bool Enumerates { get; set; } = true;

    public FakeWindowCalls With(FakeWindow window)
    {
        _windows.Add(window);
        return this;
    }

    public IReadOnlyList<nint>? TopLevel() => Enumerates ? [.. _windows.Select(window => window.Handle)] : null;

    public int? ProcessOf(nint window) => Find(window).ProcessId;

    public string? ClassOf(nint window) => Find(window).ClassName;

    public bool? IsOwned(nint window) => Find(window).Owned;

    public bool IsVisible(nint window) => Find(window).Visible;

    public bool? IsCloaked(nint window) => Find(window).Cloaked;

    public bool Exists(nint window) => Find(window).Exists;

    private FakeWindow Find(nint window) => _windows.Single(candidate => candidate.Handle == window);
}

/// <summary>One top-level window. Every member defaults to a window that qualifies.</summary>
internal sealed class FakeWindow
{
    public required nint Handle { get; init; }

    /// <summary>Null where the window has gone, which is what Windows answers for a handle that is no longer one.</summary>
    public int? ProcessId { get; init; }

    public string? ClassName { get; init; } = "AnApplicationWindow";

    public bool? Owned { get; init; } = false;

    public bool Visible { get; init; } = true;

    public bool? Cloaked { get; init; } = false;

    /// <summary>Whether it is still a window, which tells a race apart from a fact Windows refused.</summary>
    public bool Exists { get; init; } = true;
}
