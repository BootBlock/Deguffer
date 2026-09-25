using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// The rule that decides which children of Steam's per-game containers may go. Anything it lets
/// through is a target, so the cases that matter are the ones it refuses.
/// </summary>
public sealed class SteamAppIdTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("440")]
    [InlineData("2357570")]
    [InlineData("4294967295")]
    public void AcceptsWhatSteamNamesAGamesFolder(string name) => Assert.True(SteamAppId.IsAppId(name));

    [Theory]
    [InlineData("")]
    [InlineData(" 440")]
    [InlineData("440 ")]
    [InlineData("+440")]
    [InlineData("-440")]
    [InlineData("440.old")]
    [InlineData("440_backup")]
    [InlineData("1,000")]
    [InlineData("0x1B8")]
    [InlineData("４４０")]
    [InlineData("4294967296")]
    [InlineData("backup")]
    public void RefusesAnythingElse(string name) => Assert.False(SteamAppId.IsAppId(name));
}
