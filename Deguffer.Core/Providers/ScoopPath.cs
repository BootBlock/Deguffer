using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A path Scoop was given, read only where Deguffer reads it as the same folder Scoop does.
///
/// <para>Scoop passes every location through its <c>Get-AbsolutePath</c>, which resolves a relative
/// value against PowerShell's current location, and then hands it to <c>Get-ChildItem</c>,
/// <c>Test-Path</c> and <c>Remove-Item</c> as <c>-Path</c>, never <c>-LiteralPath</c>
/// (<c>lib/core.ps1</c> and <c>libexec/scoop-cache.ps1</c> at
/// <c>e6aa3b366bdee8ed138c1e0f7b85192ebdd35d0f</c>). Each is a way for the folder Deguffer examined
/// and the folder Scoop's command empties to differ, and the command removes every entry it lists
/// without looking at it, so a value where they could differ is declined rather than interpreted.</para>
/// </summary>
internal static class ScoopPath
{
    /// <summary>
    /// <c>[</c> and <c>]</c> open a wildcard class for <c>-Path</c>, so <c>D:\a[1]\cache</c> lists and
    /// removes in <c>D:\a1\cache</c>. <c>*</c> and <c>?</c> cannot appear in a Windows path at all.
    /// </summary>
    private static readonly char[] Wildcards = ['[', ']'];

    /// <summary>
    /// The folder <paramref name="value"/> names, normalised, or null where Scoop might read it as
    /// another one: not a full path, so relative to whatever folder PowerShell started in; padded
    /// with spaces, which Scoop keeps and <see cref="LongPath.Configured"/> trims; already in a
    /// device form, which PowerShell's provider paths do not treat as Win32 does; or carrying a
    /// wildcard character.
    /// </summary>
    public static string? Followed(string value)
    {
        if (value.Length != value.Trim().Length
            || value.StartsWith(@"\\?\", StringComparison.Ordinal)
            || value.StartsWith(@"\\.\", StringComparison.Ordinal)
            || HasWildcard(value))
        {
            return null;
        }

        return LongPath.Configured(value);
    }

    /// <summary>Whether <c>-Path</c> would read <paramref name="path"/> as a pattern.</summary>
    public static bool HasWildcard(string path) => path.IndexOfAny(Wildcards) >= 0;
}
