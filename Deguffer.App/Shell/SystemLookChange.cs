namespace Deguffer.App.Shell;

/// <summary>Which parts of <see cref="SystemLookState"/> a change of the system's settings moved.</summary>
[Flags]
internal enum SystemLookChange
{
    None = 0,

    /// <summary>High contrast turned on or off or to another theme, or the accent colour changed.</summary>
    Colours = 1,

    /// <summary>The reader's Windows text size changed.</summary>
    TextScale = 2,

    /// <summary>The compositor's word on whether effects are fast changed, which a remote session does.</summary>
    Effects = 4,
}
