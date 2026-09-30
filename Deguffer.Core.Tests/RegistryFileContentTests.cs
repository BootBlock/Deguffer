using Deguffer.Core.InstalledApps;

namespace Deguffer.Core.Tests;

/// <summary>
/// Reading what a <c>.reg</c> file would do before <c>reg.exe import</c> runs it (§7.3). A file is a
/// backup of one entry only where every section writes that entry and nothing is deleted.
/// </summary>
public sealed class RegistryFileContentTests
{
    private const string Key = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Tool";

    private static RegistryFileContent? Parse(params string[] body) =>
        RegistryFileContent.Parse(string.Join("\r\n", ["Windows Registry Editor Version 5.00", "", .. body, ""]));

    [Fact]
    public void AnExportOfOneEntryWithItsSubkeysIsConfined()
    {
        var content = Parse(
            $"[{Key}]",
            "\"DisplayName\"=\"Tool \\\"Pro\\\" \\\\ Edition\"",
            "\"Blob\"=hex:01,02,\\",
            "  03,04",
            "@=\"a=-b\"",
            "",
            $@"[{Key}\Child]",
            "\"x\"=dword:00000001");

        Assert.Equal(new RegistryFileContent(Key, "Tool \"Pro\" \\ Edition", IsConfined: true), content);
    }

    [Theory]
    [InlineData(@"[HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Other]")]
    [InlineData(@"[HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ToolX]")]
    [InlineData(@"[-HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Tool\Child]")]
    [InlineData("\"DisplayName\"=-")]
    [InlineData("@=-")]
    [InlineData("stray text reg.exe might read")]
    public void AnythingBeyondTheEntryOrAnyDeletionIsNotConfined(string line)
    {
        Assert.False(Parse($"[{Key}]", line)!.IsConfined);
    }

    /// <summary>The line that once sliced out of range and took the whole backup list with it.</summary>
    [Fact]
    public void ADisplayNameLineWithNoClosingQuoteIsNotAName()
    {
        var content = Parse($"[{Key}]", "\"DisplayName\"=\"");

        Assert.Null(content!.DisplayName);
    }

    [Fact]
    public void TextWithoutTheExportHeaderIsNotARegistryFile()
    {
        Assert.Null(RegistryFileContent.Parse($"[{Key}]\r\n\"DisplayName\"=\"Tool\"\r\n"));
    }

    [Fact]
    public void AFileWithNoSectionIsNotABackup()
    {
        Assert.Null(Parse());
    }
}
