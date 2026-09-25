using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which arguments of a command line count as paths it was started with. The command lines here
/// have the shapes observed from real test browsers, with the paths replaced by invented ones.
/// </summary>
public sealed class CommandLinePathsTests
{
    [Fact]
    public void ReadsAPathGivenAsTheValueOfASwitch()
    {
        var paths = CommandLinePaths.Of(
            @"""C:\Tools\chrome-headless-shell.exe"" --no-sandbox --user-data-dir=C:\Users\testuser\AppData\Local\Temp\playwright_chromiumdev_profile-a1B2c3 --remote-debugging-pipe");

        Assert.Equal([@"C:\Users\testuser\AppData\Local\Temp\playwright_chromiumdev_profile-a1B2c3"], paths);
    }

    [Fact]
    public void ReadsAPathGivenAsTheArgumentAfterASwitch()
    {
        var paths = CommandLinePaths.Of(
            @"C:\Tools\firefox.exe -no-remote -headless -profile C:\Users\testuser\AppData\Local\Temp\playwright_firefoxdev_profile-d4E5f6 -juggler-pipe -silent");

        Assert.Equal([@"C:\Users\testuser\AppData\Local\Temp\playwright_firefoxdev_profile-d4E5f6"], paths);
    }

    /// <summary>
    /// A quoted path with a space in it arrives whole. Split on spaces instead, the veto would
    /// compare two fragments that match nothing.
    /// </summary>
    [Fact]
    public void KeepsAQuotedPathWithSpacesWhole()
    {
        var paths = CommandLinePaths.Of(
            @"C:\Tools\chrome.exe ""--user-data-dir=C:\Users\test user\Temp\profile-x"" --profile ""C:\Users\test user\Temp\other""");

        Assert.Equal([@"C:\Users\test user\Temp\profile-x", @"C:\Users\test user\Temp\other"], paths);
    }

    /// <summary>
    /// The program's own path is not an argument it was started with, and a relative path names a
    /// place this cannot resolve.
    /// </summary>
    [Fact]
    public void LeavesOutTheProgramItselfAndAnythingNotFullyQualified()
    {
        var paths = CommandLinePaths.Of(@"C:\Tools\node.exe relative\script.js --out=build --flag C:relative");

        Assert.Empty(paths);
    }

    [Fact]
    public void AnEmptyCommandLineNamesNothing()
    {
        Assert.Empty(CommandLinePaths.Of(""));
        Assert.Empty(CommandLinePaths.Of("   "));
    }
}
