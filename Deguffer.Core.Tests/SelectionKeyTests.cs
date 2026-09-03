using Deguffer.Core.Execution;

namespace Deguffer.Core.Tests;

/// <summary>
/// What identifies a step from one scan to the next. A key that moves when nothing about the step
/// has really changed loses the user's choice about it, and it loses it back towards the
/// pre-selected default.
/// </summary>
public class SelectionKeyTests
{
    [Fact]
    public void KeysADeletionByItsPathRatherThanByWhatTheUserIsTold()
    {
        var before = new DeleteDirectoryStep(@"C:\Users\testuser\.gradle\caches", "Gradle build cache");
        var after = new DeleteDirectoryStep(@"C:\Users\testuser\.gradle\caches", "Cached Gradle build output");

        // Rewording the sentence beside a row is a content change, and it must not silently discard
        // every choice the user has made about it.
        Assert.NotEqual(before.Description, after.Description);
        Assert.Equal(before.SelectionKey, after.SelectionKey);
    }

    [Fact]
    public void KeepsTwoDeletionsApart()
    {
        var alpha = new DeleteDirectoryStep(@"C:\Users\testuser\src\alpha\node_modules", "Installed packages");
        var beta = new DeleteDirectoryStep(@"C:\Users\testuser\src\beta\node_modules", "Installed packages");

        Assert.NotEqual(alpha.SelectionKey, beta.SelectionKey);
    }

    /// <summary>
    /// A file and a directory at one path is not a case that arises, but the two kinds of deletion
    /// answer through the same base member, so a key that varied by type would be a surprise.
    /// </summary>
    [Fact]
    public void KeysEveryKindOfDeletionTheSameWay()
    {
        Assert.Equal(
            new DeleteFileStep(@"C:\Windows\MEMORY.DMP", "Complete memory dump").SelectionKey,
            new DeleteDirectoryStep(@"C:\Windows\MEMORY.DMP", "Complete memory dump").SelectionKey);
    }

    /// <summary>
    /// A tool that moves — an upgrade landing under a new version directory, a different PATH entry
    /// resolving first — is still running the same command against the same cache.
    /// </summary>
    [Fact]
    public void KeysACommandWithoutWhereTheToolIsInstalled()
    {
        var before = new RunCommandStep(
            @"C:\Program Files\nodejs\npm.cmd", "cache clean --force", "Clear the npm cache");
        var after = new RunCommandStep(
            @"C:\Users\testuser\AppData\Roaming\nvm\v22.0.0\npm.cmd", "cache clean --force", "Clear the npm cache");

        Assert.Equal("npm.cmd cache clean --force", before.SelectionKey);
        Assert.Equal(before.SelectionKey, after.SelectionKey);
    }

    [Fact]
    public void KeepsTwoCommandsOfOneToolApart()
    {
        Assert.NotEqual(
            new RunCommandStep("dotnet.exe", "nuget locals http-cache --clear", "Clear the HTTP cache").SelectionKey,
            new RunCommandStep("dotnet.exe", "nuget locals temp --clear", "Clear the temporary files").SelectionKey);
    }
}
