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
    private readonly List<nint> _posted = [];

    /// <summary>False where Windows refuses to enumerate them at all.</summary>
    public bool Enumerates { get; set; } = true;

    /// <summary>The shell's own window, or null where a test says Windows reports none.</summary>
    public nint? Shell { get; set; }

    /// <summary>False where Windows refuses the post, which §7.2.1 records rather than acts on.</summary>
    public bool PostSucceeds { get; set; } = true;

    /// <summary>
    /// What the machine does at the instant a message goes out. The one moment a close cannot be
    /// called off from, and nothing else in a test can reach it.
    /// </summary>
    public Action? WhenPosted { get; set; }

    /// <summary>
    /// Every window a close was posted to, in order. It is what proves a window whose owner changed
    /// between the survey and the post received nothing.
    /// </summary>
    public IReadOnlyList<nint> Posted => _posted;

    public FakeWindowCalls With(FakeWindow window)
    {
        _windows.Add(window);
        return this;
    }

    public IReadOnlyList<nint>? TopLevel() => Enumerates ? [.. _windows.Select(window => window.Handle)] : null;

    public int? ProcessOf(nint window) => Find(window).Owner();

    public string? ClassOf(nint window) => Find(window).ClassName;

    public bool? IsOwned(nint window) => Find(window).Owned;

    public bool IsVisible(nint window) => Find(window).Visible;

    public bool? IsCloaked(nint window) => Find(window).Cloaked;

    public bool Exists(nint window) => Find(window).Exists;

    public nint? ShellWindow() => Shell;

    public bool PostClose(nint window)
    {
        _posted.Add(window);
        WhenPosted?.Invoke();

        return PostSucceeds;
    }

    private FakeWindow Find(nint window) => _windows.Single(candidate => candidate.Handle == window);
}

/// <summary>
/// One top-level window. Every member a test does not set describes a window that qualifies, except
/// the two it must name: which window it is, and whose.
/// </summary>
internal sealed class FakeWindow
{
    public required nint Handle { get; init; }

    /// <summary>
    /// Whose window it is. Null where Windows would not say, which it does for a handle that is no
    /// longer a window.
    /// </summary>
    public required int? ProcessId { get; init; }

    /// <summary>
    /// The owners this window reports, one per question, with the last repeating. §7.2.1 asks again
    /// immediately before each message because a window handle is recycled, and this is how a test
    /// makes a window change hands between the survey and the post.
    /// </summary>
    public IReadOnlyList<int?>? Owners { get; init; }

    private int _asked;

    internal int? Owner()
    {
        if (Owners is null)
        {
            return ProcessId;
        }

        var owner = Owners[Math.Min(_asked, Owners.Count - 1)];
        _asked++;

        return owner;
    }

    public string? ClassName { get; init; } = "AnApplicationWindow";

    public bool? Owned { get; init; } = false;

    public bool Visible { get; init; } = true;

    public bool? Cloaked { get; init; } = false;

    /// <summary>Whether it is still a window, which tells a race apart from a fact Windows refused.</summary>
    public bool Exists { get; init; } = true;
}
