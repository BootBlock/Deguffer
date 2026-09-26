using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// The After Effects preferences format, as real files write it: quoted fragments with UTF-8 bytes in
/// hexadecimal between them, lines split with a trailing backslash, and whichever line ends the
/// platform uses. A misread here names the wrong folder to look in for something to delete, so a
/// value that cannot be read must come back as no value at all.
/// </summary>
public sealed class AfterEffectsPreferenceTextTests
{
    private const string Section = AfterEffectsDiskCacheLayout.PreferencesSection;

    /// <summary>The shape of a real file around the setting, with an invented folder.</summary>
    private static string File(string folderLine, string newline = "\n") => string.Join(newline,
        "# Text File Version 1.1",
        "# After Effects Preferences",
        "",
        "[\"Additional Disk Cache Controls\"]",
        "\t\"Minimum Volume Free Space (Gigabytes)\" = \"10\"",
        "",
        "[\"Disk Cache Controls\"]",
        "\t\"Enabled 2\" = \"1\"",
        folderLine,
        "\t\"Max Size 3\" = \"23\"",
        "",
        "[\"Expression Editor Settings (v9)\"]",
        "\t\"Auto Complete - Enabled\" = 01",
        "");

    private static IReadOnlyList<string> Folders(string text) =>
        [.. AfterEffectsPreferenceText.Values(text, Section, AfterEffectsDiskCacheLayout.IsFolderKey).Select(v => v.Value)];

    /// <summary>A Windows install writes line feeds, and a macOS one a carriage return alone.</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void ReadsTheFolderWhateverTheLineEnds(string newline)
    {
        var text = File("\t\"Folder 7\" = \"C:\\Users\\testuser\\AppData\\Local\\Temp\"", newline);

        Assert.Equal([@"C:\Users\testuser\AppData\Local\Temp"], Folders(text));
    }

    /// <summary>
    /// A Russian install writes Cyrillic as UTF-8 bytes between the quotes, so a profile folder with an
    /// accented name is read the same way.
    /// </summary>
    [Fact]
    public void DecodesTheBytesWrittenBetweenTheQuotes()
    {
        var text = File("\t\"Folder 7\" = \"D:\\Jos\"C3A9\"\\\"D09AD18DD188\"\"");

        Assert.Equal(["D:\\José\\Кэш"], Folders(text));
    }

    /// <summary>A long value is split with a backslash outside the quotes and carried on, indented.</summary>
    [Fact]
    public void JoinsAValueSplitAcrossLines()
    {
        var text = File("\t\"Folder 7\" = \"D:\\Media\\After Effects\\Very Long Cache Fo\"\\\r\t\t\"lder Name\"", "\r");

        Assert.Equal([@"D:\Media\After Effects\Very Long Cache Folder Name"], Folders(text));
    }

    /// <summary>A path ending in a separator ends in a quote, so it does not continue onto the next line.</summary>
    [Fact]
    public void ABackslashInsideTheQuotesIsPartOfTheValue()
    {
        var text = File("\t\"Folder 7\" = \"D:\\\"");

        Assert.Equal([@"D:\"], Folders(text));
        Assert.Contains(
            AfterEffectsPreferenceText.Values(text, Section, key => true),
            value => value is { Key: "Max Size 3", Value: "23" });
    }

    [Theory]
    [InlineData("\t\"Folder 7\" = \"D:\\Jos\"C3\"\"", "bytes that are not UTF-8")]
    [InlineData("\t\"Folder 7\" = \"D:\\Cache", "a quote that never closes")]
    [InlineData("\t\"Folder 7\" = D:\\Cache", "text outside the quotes")]
    [InlineData("\t\"Folder 7\" = \"D:\\Cach\u00e9\"", "a byte past ASCII inside the quotes")]
    public void GivesNoValueForOneItCannotRead(string line, string because)
    {
        Assert.True(Folders(File(line)).Count == 0, because);
    }

    [Fact]
    public void ReadsOnlyTheNamedSection()
    {
        var text = string.Join("\n",
            "[\"Other Controls\"]",
            "\t\"Folder 7\" = \"D:\\Elsewhere\"",
            "[\"Disk Cache Controls\"]",
            "\t\"Folder 7\" = \"D:\\Cache\"",
            "[\"Later\"]",
            "\t\"Folder 7\" = \"D:\\Later\"");

        Assert.Equal([@"D:\Cache"], Folders(text));
    }

    [Theory]
    [InlineData("Folder 7", true)]
    [InlineData("Folder 6", true)]
    [InlineData("Folder 12", true)]
    [InlineData("Folder", false)]
    [InlineData("Folder ", false)]
    [InlineData("Folder 7b", false)]
    [InlineData("Folders 7", false)]
    [InlineData("Max Size 3", false)]
    public void RecognisesEveryNumberedFolderKey(string key, bool expected) =>
        Assert.Equal(expected, AfterEffectsDiskCacheLayout.IsFolderKey(key));
}
