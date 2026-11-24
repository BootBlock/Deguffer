using Deguffer.App.Shell;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;

namespace Deguffer.App.Tests;

/// <summary>
/// Copy details puts what the version card shows on the clipboard for a bug report, which is pasted
/// into a public issue, so it carries every field the card shows and nothing that names the user or
/// the machine. That the button puts it on the clipboard is observed by driving the page.
/// </summary>
public sealed class BuildDetailsTests
{
    private static readonly BuildDetails Native = new(
        "1.2.3-beta",
        new DateOnly(2026, 11, 24),
        RuntimeArchitecture.X64,
        RuntimeArchitecture.X64,
        new Version(10, 0, 26100, 0),
        IsElevated: false);

    [Fact]
    public void TheCopiedTextCarriesEveryFieldTheCardShows()
    {
        var text = Native.ToClipboardText();

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Deguffer 1.2.3-beta",
                "Built: 2026-11-24",
                "Architecture: x64",
                "Windows: Windows 11 (10.0.26100)",
                "Scanning: By walking each location, without administrator rights"),
            text);
        Assert.All(
            [Native.Version, Native.ArchitectureText, Native.WindowsText, Native.ScanModeText],
            field => Assert.Contains(field, text, StringComparison.Ordinal));
    }

    /// <summary>A build run under emulation says so, because that is what a slow report most needs.</summary>
    [Fact]
    public void AForeignBuildNamesTheMachinesProcessorToo() =>
        Assert.Contains(
            "Architecture: x64, on an ARM64 processor",
            (Native with { Machine = RuntimeArchitecture.Arm64 }).ToClipboardText(),
            StringComparison.Ordinal);

    [Fact]
    public void AnElevatedProcessSaysItReadsTheFileTable() =>
        Assert.Contains(
            "Scanning: From the volume's file table, as administrator",
            (Native with { IsElevated = true }).ToClipboardText(),
            StringComparison.Ordinal);

    [Fact]
    public void AnUnstampedBuildSaysItsDateIsNotKnown()
    {
        var unstamped = Native with { BuildDate = null };

        Assert.Contains("Built: not known", unstamped.ToClipboardText(), StringComparison.Ordinal);
        Assert.Equal("not known", unstamped.BuildDateText);
    }

    /// <summary>Windows 11 reports itself as version 10.0, so only its build number tells the two apart.</summary>
    [Theory]
    [InlineData(19045, "Windows 10 (10.0.19045)")]
    [InlineData(21999, "Windows 10 (10.0.21999)")]
    [InlineData(22000, "Windows 11 (10.0.22000)")]
    [InlineData(26100, "Windows 11 (10.0.26100)")]
    public void WindowsIsNamedFromItsBuild(int build, string named) =>
        Assert.Equal(named, (Native with { WindowsVersion = new Version(10, 0, build, 0) }).WindowsText);

    /// <summary>
    /// Every field is built from a version, a date, two processor families, a version number and
    /// whether the process is elevated, so nothing that names a folder can reach the text.
    /// </summary>
    [Fact]
    public void TheCopiedTextHoldsNoPath()
    {
        var text = (Native with { Machine = RuntimeArchitecture.Arm64, IsElevated = true }).ToClipboardText();

        Assert.DoesNotContain(@"\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%", text, StringComparison.Ordinal);
    }

    /// <summary>A report that names the platform once names it as the copied lines do.</summary>
    [Fact]
    public void ThePlatformLineNamesWindowsThenTheArchitecture() =>
        Assert.Equal(
            "Windows 11 (10.0.26100), x64, on an ARM64 processor",
            (Native with { Machine = RuntimeArchitecture.Arm64 }).PlatformText);
}
