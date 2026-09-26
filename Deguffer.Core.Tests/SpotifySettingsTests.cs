using System.Text;
using Deguffer.Core.Providers;
using Deguffer.Testing;

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
    /// mark and Windows line endings. A location is the first line, so the byte order mark sits in
    /// front of its key and the key matches only once the mark is gone. The saved sign-in beside
    /// them is a quoted value full of escaped quotes, and it is neither read nor mistaken for a
    /// location.
    /// </summary>
    [Fact]
    public void ReadsBothKeysUnescapedAndNormalised()
    {
        var path = _temp.CreateFile(0, "prefs");
        File.WriteAllText(
            path,
            string.Join(
                "\r\n",
                @"storage.location=""D:\\Music\\Spotify\\""",
                "autologin.saved_credentials=\"{\\\"storage.location\\\":\\\"relative\\\"}\"",
                @"storage.last-location = ""\\\\server\\share\\Spotify""",
                "storage.size=1024"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var settings = SpotifySettings.Read(path);

        Assert.Equal(SpotifySettingsReading.Read, settings.Reading);
        Assert.Equal(new[] { @"D:\Music\Spotify", @"\\server\share\Spotify" }, settings.Locations);
    }

    /// <summary>
    /// A file whose lines end with a carriage return alone. Split on line feeds only, it is one line,
    /// the key that matches is the first line's, and a location on any later line reads as "nothing
    /// moved". So the location here is not on the first line.
    /// </summary>
    [Fact]
    public void ReadsAFileWhoseLinesEndWithACarriageReturnAlone()
    {
        var settings = SpotifySettings.Read(WriteSettings(
            "app.autostart-mode=\"off\"\rstorage.location=\"D:\\\\Spotify\"\r"));

        Assert.Equal(SpotifySettingsReading.Read, settings.Reading);
        Assert.Equal(new[] { @"D:\Spotify" }, settings.Locations);
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
    /// is.
    /// </summary>
    [Theory]
    [InlineData("storage.location=\"relative\\\\folder\"")]
    [InlineData("storage.location=D:\\\\unquoted")]
    [InlineData("storage.location=\"D:\\\\music\\q\"")]
    [InlineData("storage.location=\"D:\\\\mu\"sic\"")]
    [InlineData("storage.location=\"D:\\\\music\\\"")]
    [InlineData("storage.location=\"")]
    public void ALocationThatCannotBePlacedLeavesTheFileUnsettled(string content)
    {
        var settings = SpotifySettings.Read(WriteSettings(content));

        Assert.Equal(SpotifySettingsReading.Uninterpretable, settings.Reading);
        Assert.False(settings.IsSettled);
        Assert.Empty(settings.Locations);
    }

    /// <summary>
    /// One good line beside a bad one does not settle the file, and the good one is not thrown away
    /// either: it still names where downloads may be, so it still has to be protected. The bad line
    /// comes first, so a reader that stopped at it would miss the good one.
    /// </summary>
    [Fact]
    public void ALocationThatCanBePlacedIsKeptBesideOneThatCannot()
    {
        var settings = SpotifySettings.Read(WriteSettings(
            "storage.location=\"relative\"\nstorage.last-location=\"D:\\\\Music\\\\Spotify\"\n"));

        Assert.Equal(SpotifySettingsReading.Uninterpretable, settings.Reading);
        Assert.Equal(new[] { @"D:\Music\Spotify" }, settings.Locations);
    }

    /// <summary>
    /// Files that are not UTF-8 text. Read leniently, the first would name a path with a replacement
    /// character in it, and the other two would match no key at all and read as "nothing moved".
    ///
    /// <para>The UTF-16 content is plain ASCII on purpose. Without a byte order mark it is then valid
    /// UTF-8 with a NUL after every character, so only the check for a NUL can refuse it. A character
    /// outside ASCII would be refused by the decoder first, and the NUL check would go untested.</para>
    /// </summary>
    [Theory]
    [InlineData("windows-1252")]
    [InlineData("utf-16 with a byte order mark")]
    [InlineData("utf-16 without a byte order mark")]
    public void AFileThatIsNotUtf8TextIsUninterpretable(string form)
    {
        const string content = "storage.location=\"D:\\\\Music\\\\Spotify\"\n";

        byte[] bytes = form switch
        {
            "windows-1252" => [.. Encoding.ASCII.GetBytes("storage.location=\"D:\\\\Mus"), 0xE9, .. Encoding.ASCII.GetBytes("e\\\\Spotify\"\n")],
            "utf-16 with a byte order mark" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(content)],
            _ => Encoding.Unicode.GetBytes(content),
        };

        var path = _temp.CreateFile(0, "prefs");
        File.WriteAllBytes(path, bytes);

        var settings = SpotifySettings.Read(path);

        Assert.Equal(SpotifySettingsReading.Uninterpretable, settings.Reading);
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
