using Deguffer.Core.ModManagers;

namespace Deguffer.Core.Tests;

/// <summary>
/// Values read back as Qt's <c>QSettings</c> reads them, because Mod Organizer 2 writes every path it keeps
/// through it, in its escaped form.
/// </summary>
public sealed class QtIniFileTests
{
    [Theory]
    [InlineData(@"C:\\Games\\Mod Organizer", @"C:\Games\Mod Organizer")]
    [InlineData("D:/Modding/downloads", "D:/Modding/downloads")]
    [InlineData("\"D:\\\\Mods, old\\\\downloads\"", @"D:\Mods, old\downloads")]
    [InlineData("\" D:\\\\padded \"", @" D:\padded ")]
    [InlineData(@"C:\\Spiel\xe4nde", @"C:\Spielände")]
    [InlineData("  %BASE_DIR%/downloads  ", "%BASE_DIR%/downloads")]
    [InlineData("say \\\"hi\\\"", "say \"hi\"")]
    public void AValueIsReadAsQtWroteIt(string written, string read)
    {
        var ini = QtIniFile.Parse($"[Settings]\r\ndownload_directory={written}\r\n");

        Assert.Equal(read, ini.Value("Settings", "download_directory"));
    }

    [Fact]
    public void SectionsAndKeysIgnoreCaseAndAnArrayKeyKeepsItsSeparator()
    {
        var ini = QtIniFile.Parse(
            """
            [General]
            installed=true
            [handlers]
            size=1
            1\executable=C:\\MO2\\ModOrganizer.exe
            """);

        Assert.True(ini.IsTrue("general", "INSTALLED"));
        Assert.False(ini.IsTrue("General", "uninstalled"));
        Assert.Equal(@"C:\MO2\ModOrganizer.exe", ini.Value("Handlers", @"1\executable"));
        Assert.Null(ini.Value("Settings", "base_directory"));
        Assert.Empty(ini.Section("Settings"));
    }

    [Fact]
    public void AKeyBeforeAnySectionIsGenerals()
    {
        var ini = QtIniFile.Parse("installed=true\r\n[General]\r\nmodID=12\r\n");

        Assert.True(ini.IsTrue(QtIniFile.General, "installed"));
        Assert.Equal("12", ini.Value(QtIniFile.General, "modID"));
    }
}
