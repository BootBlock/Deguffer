namespace Deguffer.Core.Safety;

/// <summary>What a file opened by <see cref="FileInformation.OpenHeld"/> is held for, which decides what every other program may do to it meanwhile.</summary>
internal enum HeldFor
{
    /// <summary>Kept: every other program may read it, and none may write to it, rename it or delete it.</summary>
    Keeping,

    /// <summary>
    /// Removed through this handle: every other program may read it, rename it or delete it, as the
    /// shell does when it moves it into the Recycle Bin, and none may write to it.
    /// </summary>
    Removing,
}
