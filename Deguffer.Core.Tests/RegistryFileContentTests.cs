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

    /// <summary>
    /// reg.exe continues only hex data onto the next line. Measured on 2026-09-30: a string value
    /// ending in a backslash, with a section on the next line, imported that section, so a check
    /// that skipped the line as a continuation vouched for a file that wrote another key.
    /// </summary>
    [Theory]
    [InlineData("\"x\"=\"y\"\\")]
    [InlineData("\"x\"=dword:00000001\\")]
    [InlineData("\"x\"=\"y\\\"")]
    [InlineData("\"x\"=\"y\" trailing")]
    public void ADataLineReadExeDoesNotContinueHidesNothing(string line)
    {
        var content = Parse($"[{Key}]", line, @"[HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run]", "\"evil\"=\"1\"");

        Assert.False(content!.IsConfined);
    }

    [Theory]
    [InlineData("\"x\"=dword:1")]
    [InlineData("\"x\"=qword:0000000000000001")]
    [InlineData("\"x\"=hex:1")]
    [InlineData("\"x\"=")]
    [InlineData("\"x\"=\"y\" trailing")]
    [InlineData("\"x\"=\"y\"\"z\"")]
    public void DataInAnyFormReadExeDoesNotWriteIsNotConfined(string line)
    {
        Assert.False(Parse($"[{Key}]", line)!.IsConfined);
    }

    [Fact]
    public void AContinuationThatIsNotHexIsNotConfined()
    {
        Assert.False(Parse($"[{Key}]", "\"Blob\"=hex:01,\\", @"  [HKEY_CURRENT_USER\SOFTWARE\Other]")!.IsConfined);
    }

    [Fact]
    public void EveryTypeReadExeExportsIsConfined()
    {
        var content = Parse(
            $"[{Key}]",
            "@=\"\"",
            "\"Empty\"=hex:",
            "\"Expand\"=hex(2):25,00,00,00",
            "\"Multi\"=hex(7):61,00,00,00,\\",
            "  00,00",
            "\"Qword\"=hex(b):01,00,00,00,00,00,00,00",
            "\"None\"=hex(0):");

        Assert.True(content!.IsConfined);
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
