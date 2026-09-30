using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.3: a path an entry names is absent only where every view of it is, and absence is proof only
/// where the path's drive answers and nothing on the way down is a link or refuses.
/// </summary>
public sealed class InstalledPathsTests
{
    private readonly FakePathProbe _probe = new();

    private InstalledPaths Paths(ISystemDirectories? system = null) => new(_probe, system ?? FixedSystemDirectories.Standard);

    [Fact]
    public void ASystemFolderPathIsAskedAboutInAllThreeOfItsNames()
    {
        Assert.Equal(
            [@"C:\Windows\System32\tool.exe", @"C:\Windows\SysWOW64\tool.exe", @"C:\Windows\Sysnative\tool.exe"],
            Paths().ViewsOf(@"C:\Windows\System32\tool.exe"));
    }

    [Fact]
    public void AProgramFolderPathIsAskedAboutInBoth()
    {
        Assert.Equal(
            [@"C:\Program Files (x86)\Tool", @"C:\Program Files\Tool"],
            Paths().ViewsOf(@"C:\Program Files (x86)\Tool"));
    }

    [Theory]
    [InlineData(@"C:\Program Files Extra\Tool")]
    [InlineData(@"C:\Windows\System32Backup\tool.exe")]
    [InlineData(@"D:\Program Files\Tool")]
    [InlineData(@"C:\Users\testuser\Apps\Tool")]
    public void AnyOtherPathIsAskedAboutAsItIs(string path)
    {
        Assert.Equal([path], Paths().ViewsOf(path));
    }

    /// <summary>A 32-bit Windows has no 32-bit program folder, and an empty name must not match every path.</summary>
    [Fact]
    public void AMissingProgramFolderIsNotAView()
    {
        var system = FixedSystemDirectories.Standard with { ProgramFilesX86 = string.Empty };

        Assert.Equal([@"C:\Program Files\Tool"], Paths(system).ViewsOf(@"C:\Program Files\Tool"));
    }

    /// <summary>A 32-bit Deguffer sees the 64-bit <c>System32</c> only as <c>Sysnative</c>.</summary>
    [Fact]
    public void AFileStandingInAnyViewIsPresent()
    {
        _probe.File(@"C:\Windows\Sysnative\tool.exe");

        Assert.Equal(PathPresence.Present, Paths().ProbeFile(@"C:\Windows\System32\tool.exe"));
    }

    [Fact]
    public void AViewWindowsRefusesMakesAnAbsentPathRefused()
    {
        _probe.Refused(@"C:\Windows\SysWOW64\tool.exe");

        Assert.Equal(PathPresence.Refused, Paths().ProbeFile(@"C:\Windows\System32\tool.exe"));
    }

    [Fact]
    public void APathAbsentInEveryViewIsAbsent()
    {
        Assert.Equal(PathPresence.Absent, Paths().ProbeEntry(@"C:\Program Files\Tool"));
    }

    [Fact]
    public void AbsenceBelowPlainFoldersIsProof()
    {
        _probe.Directory(@"C:\Program Files").Directory(@"C:\Program Files (x86)");

        Assert.Null(Paths().WhyAbsenceProvesNothing(@"C:\Program Files\Tool\unins000.exe"));
    }

    [Fact]
    public void ADriveThatIsNotConnectedMakesAbsenceProveNothing()
    {
        _probe.Disconnected(@"E:\");

        Assert.Contains(@"is on E:\, which is not connected", Paths().WhyAbsenceProvesNothing(@"E:\Tool\unins000.exe"), StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkOnTheWayMakesAbsenceProveNothing()
    {
        _probe.Directory(@"C:\Games", isLink: true);

        Assert.Contains(@"C:\Games is a link", Paths().WhyAbsenceProvesNothing(@"C:\Games\Tool\unins000.exe"), StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderOnTheWayThatRefusesMakesAbsenceProveNothing()
    {
        _probe.Directory(@"C:\Games").Refused(@"C:\Games\Tool");

        Assert.Contains(@"would not say what C:\Games\Tool is", Paths().WhyAbsenceProvesNothing(@"C:\Games\Tool\unins000.exe"), StringComparison.Ordinal);
    }

    /// <summary>A share answers by its root, <c>\\server\share</c>, as a drive answers by its letter.</summary>
    [Fact]
    public void AShareThatDoesNotAnswerMakesAbsenceProveNothing()
    {
        _probe.Disconnected(@"\\server.test\apps");

        Assert.Contains(@"is on \\server.test\apps", Paths().WhyAbsenceProvesNothing(@"\\server.test\apps\Tool\unins000.exe"), StringComparison.Ordinal);
    }

    /// <summary>Every view is walked: the one this process would not look through may be the link.</summary>
    [Fact]
    public void ALinkInAnotherViewMakesAbsenceProveNothing()
    {
        _probe.Directory(@"C:\Windows").Directory(@"C:\Windows\SysWOW64", isLink: true);

        Assert.Contains(@"C:\Windows\SysWOW64 is a link", Paths().WhyAbsenceProvesNothing(@"C:\Windows\System32\tool.exe"), StringComparison.Ordinal);
    }
}
