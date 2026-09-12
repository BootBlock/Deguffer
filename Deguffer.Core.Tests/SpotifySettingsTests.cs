using System.Text;
using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Reading where Spotify's settings file says its storage is. What matters most is the direction of
/// every failure: a value that cannot be placed must never read as "nothing moved".
/// </summary>
public sealed class SpotifySettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string WriteSettings(string content)
    {
        var path = _temp.CreateFile(0, "prefs");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void AMissingFileIsAbsent()
    {
        var settings = SpotifySettings.Read(Path.Combine(_temp.Path, "prefs"));

        Assert.Equal(SpotifySettingsReading.Absent, settings.Reading);
        Assert.True(settings.IsSettled);
        Assert.Empty(settings.Locations);
    }

    /// <summary>
    /// Both keys, with the escapes removed and the paths normalised, from a file carrying a byte order
    /// mark and Windows line endings. The saved sign-in beside them is a quoted value full of escaped
    /// quotes, and it is neither read nor mistaken for a location.
    /// </summary>
    [Fact]
    public void ReadsBothKeysUnescapedAndNormalised()
    {
        var path = _temp.CreateFile(0, "prefs");
        File.WriteAllText(
            path,
            string.Join(
                "\r\n",
                "autologin.saved_credentials=\"{\\\"storage.location\\\":\\\"relative\\\"}\"",
                @"storage.location=""D:\\Music\\Spotify\\""",
                @"storage.last-location = ""\\\\server\\share\\Spotify""",
                "storage.size=1024"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var settings = SpotifySettings.Read(path);

        Assert.Equal(SpotifySettingsReading.Read, settings.Reading);
        Assert.Equal(new[] { @"D:\Music\Spotify", @"\\server\share\Spotify" }, settings.Locations);
    }

    [Fact]
    public void TheSameLocationUnderBothKeysIsNamedOnce()
    {
        var settings = SpotifySettings.Read(WriteSettings(
            "storage.location=\"D:\\\\Spotify\"\nstorage.last-location=\"d:\\\\spotify\"\n"));

        Assert.Equal(new[] { @"D:\Spotify" }, settings.Locations);
    }

    [Theory]
    [InlineData("storage.location=\"\"\n")]
    [InlineData("xstorage.location=\"relative\"\n")]
    [InlineData("storage.locations=\"relative\"\n")]
    [InlineData("app.autostart-mode=\"off\"\nstorage.size=1024\n")]
    [InlineData("")]
    public void ALineThatNamesNoLocationMovesNothing(string content)
    {
        var settings = SpotifySettings.Read(WriteSettings(content));

        Assert.Equal(SpotifySettingsReading.Read, settings.Reading);
        Assert.Empty(settings.Locations);
    }

    /// <summary>
    /// Each of these names a location Deguffer cannot place, so the file cannot say where the storage
    /// is. One good line beside a bad one does not settle it.
    /// </summary>
    [Theory]
    [InlineData("storage.location=\"relative\\\\folder\"")]
    [InlineData("storage.location=D:\\\\unquoted")]
    [InlineData("storage.location=\"D:\\\\music\\q\"")]
    [InlineData("storage.location=\"D:\\\\mu\"sic\"")]
    [InlineData("storage.location=\"D:\\\\music\\\"")]
    [InlineData("storage.location=\"")]
    [InlineData("storage.last-location=\"D:\\\\ok\"\nstorage.location=\"relative\"")]
    public void ALocationThatCannotBePlacedLeavesTheFileUnsettled(string content)
    {
        var settings = SpotifySettings.Read(WriteSettings(content));

        Assert.Equal(SpotifySettingsReading.Uninterpretable, settings.Reading);
        Assert.False(settings.IsSettled);
        Assert.Empty(settings.Locations);
    }

    [Fact]
    public void AFileLargerThanSpotifyWritesIsUnreadable()
    {
        var path = _temp.CreateFile((1024 * 1024) + 1, "prefs");

        var settings = SpotifySettings.Read(path);

        Assert.Equal(SpotifySettingsReading.Unreadable, settings.Reading);
        Assert.False(settings.IsSettled);
    }

    [Fact]
    public void ALockedFileIsUnreadable()
    {
        var path = WriteSettings("storage.location=\"D:\\\\Spotify\"\n");

        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(SpotifySettingsReading.Unreadable, SpotifySettings.Read(path).Reading);
    }
}
