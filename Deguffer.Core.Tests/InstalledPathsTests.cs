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

    private InstalledPaths Paths(ISystemDirectories? system = null, IVolumeInventory? volumes = null) =>
        new(_probe, system ?? FixedSystemDirectories.Standard, volumes ?? new FakeVolumeInventory());

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

    /// <summary>
    /// A value written as <c>REG_SZ</c> is not expanded, so the probe asks about a folder literally
    /// named <c>%USERNAME%</c>, which is absent wherever the program is.
    /// </summary>
    [Fact]
    public void APathThatStillNamesAVariableMakesAbsenceProveNothing()
    {
        Assert.Contains(
            "names %USERNAME%, which Windows did not expand",
            Paths().WhyAbsenceProvesNothing(@"C:\Users\%USERNAME%\AppData\Local\Programs\Tool\unins000.exe"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ALonePercentSignIsNotAVariable()
    {
        Assert.Null(Paths().WhyAbsenceProvesNothing(@"C:\Tools\100% Tool\unins000.exe"));
    }

    /// <summary>Every view is walked: the one this process would not look through may be the link.</summary>
    [Fact]
    public void ALinkInAnotherViewMakesAbsenceProveNothing()
    {
        _probe.Directory(@"C:\Windows").Directory(@"C:\Windows\SysWOW64", isLink: true);

        Assert.Contains(@"C:\Windows\SysWOW64 is a link", Paths().WhyAbsenceProvesNothing(@"C:\Windows\System32\tool.exe"), StringComparison.Ordinal);
    }

    /// <summary>
    /// An entry naming the system folder through a letter <c>subst</c> made for <c>C:\Windows</c> names
    /// a path below <c>System32</c>, which its text does not say. It is asked about in every name of that
    /// folder, so its absence through the letter alone does not read as the program gone.
    /// </summary>
    [Fact]
    public void ASystemFolderReachedThroughASubstitutedLetterIsAskedAboutInAllThreeOfItsNames()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\").Substituting(@"S:\", @"C:\Windows");

        Assert.Equal(
            [@"S:\System32\tool.exe", @"C:\Windows\System32\tool.exe", @"C:\Windows\SysWOW64\tool.exe", @"C:\Windows\Sysnative\tool.exe"],
            Paths(volumes: volumes).ViewsOf(@"S:\System32\tool.exe"));
    }

    /// <summary>The system volume also mounted at a folder puts both program folders below that folder too.</summary>
    [Fact]
    public void AProgramFolderReachedThroughAnotherMountIsAskedAboutInBoth()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\", alsoMountedAt: [@"D:\SysMount\"]);

        Assert.Equal(
            [@"D:\SysMount\Program Files (x86)\Tool", @"C:\Program Files\Tool", @"C:\Program Files (x86)\Tool"],
            Paths(volumes: volumes).ViewsOf(@"D:\SysMount\Program Files (x86)\Tool"));
    }

    /// <summary>
    /// What the views are for: a 32-bit uninstaller standing in <c>SysWOW64</c>, named through the letter
    /// as the native folder, is there.
    /// </summary>
    [Fact]
    public void AFileStandingInAnyViewOfAPathThroughALetterIsPresent()
    {
        var volumes = new FakeVolumeInventory().With(@"C:\").Substituting(@"S:\", @"C:\Windows");
        _probe.File(@"C:\Windows\SysWOW64\tool.exe");

        Assert.Equal(PathPresence.Present, Paths(volumes: volumes).ProbeFile(@"S:\System32\tool.exe"));
    }

    /// <summary>An ordinary folder reached through a letter or another mount has no other view, as before.</summary>
    [Theory]
    [InlineData(@"S:\Apps\Tool")]
    [InlineData(@"D:\SysMount\Users\testuser\Apps\Tool")]
    public void AnOrdinaryFolderReachedAnotherWayIsAskedAboutAsItIs(string path)
    {
        var volumes = new FakeVolumeInventory()
            .With(@"C:\", alsoMountedAt: [@"D:\SysMount\"])
            .Substituting(@"S:\", @"C:\Users\testuser");

        Assert.Equal([path], Paths(volumes: volumes).ViewsOf(path));
    }
}
