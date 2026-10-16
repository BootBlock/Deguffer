namespace Deguffer.Core.Tests;

internal static class DriveLetters
{
    /// <summary>
    /// A drive letter no volume answers to, so a scan rooted at it reaches the route choice and then
    /// finds nothing to walk. Without it, testing the volume-root branch would mean walking a real
    /// disk.
    /// </summary>
    public static char Unused() =>
        "ZYXWVUTSRQPONM".First(letter => !Directory.Exists($@"{letter}:\"));
}
