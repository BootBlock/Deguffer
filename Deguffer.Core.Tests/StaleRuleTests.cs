using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.3's stale rule: an entry is stale only when the machine proves what it describes is gone, and
/// a refusal is never proof. A wrong "stale" is the one verdict that removes something still there,
/// so most of these assert that short of proof the entry stays installed.
/// </summary>
public sealed class StaleRuleTests
{
    private const string ProductKey = "{12345678-9ABC-DEF0-1234-56789ABCDEF0}";

    private const string Folder = @"C:\Program Files\Tool";

    private static readonly Guid Product = Guid.Parse(ProductKey);

    private readonly FakeWindowsInstaller _installer = new();

    private readonly FakePathProbe _paths = new();

    private StandingVerdict Decide(UninstallRecord record, UninstallCommand command) =>
        StaleRule.Decide(record, command, new StaleEvidence(
            _installer,
            new InstalledPaths(_paths, FixedSystemDirectories.Standard),
            new FakePackageDependencies(),
            _ => PathPresence.Absent));

    private static UninstallRecord Record(string name, params (string Name, object Value)[] values) =>
        new(new UninstallKey(UninstallScope.Machine64, name), new UninstallValues(values.ToDictionary(v => v.Name, v => v.Value)));

    private static ProgramCommand Uninstaller(PathPresence presence) =>
        new($"\"{Folder}\\unins000.exe\"", $@"{Folder}\unins000.exe", string.Empty, presence);

    [Fact]
    public void AnInstallerProductWindowsInstallerDoesNotKnowIsStale()
    {
        _installer.With(Product, InstallerProductState.Unknown);

        var verdict = Decide(Record(ProductKey, ("WindowsInstaller", 1)), new NamedCommand("MsiExec.exe /I", "MsiExec.exe", "/I"));

        Assert.Equal(EntryStanding.Stale, verdict.Standing);
        Assert.Contains("Windows Installer does not know this product", verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InstallerProductState.Installed, EntryStanding.Installed)]
    [InlineData(InstallerProductState.Advertised, EntryStanding.Installed)]
    [InlineData(InstallerProductState.OtherAccount, EntryStanding.OtherAccount)]
    [InlineData(InstallerProductState.Unanswered, EntryStanding.Unproven)]
    public void AnyOtherInstallerAnswerKeepsTheEntry(InstallerProductState state, EntryStanding expected)
    {
        _installer.With(Product, state);

        Assert.Equal(expected, Decide(Record(ProductKey, ("WindowsInstaller", 1)), MissingCommand.Instance).Standing);
    }

    /// <summary>
    /// A key named like a product code is not a Windows Installer entry by its name alone: the
    /// measured machine had 45 that were not. Without the flag the uninstaller is what is asked.
    /// </summary>
    [Fact]
    public void AKeyNamedLikeAProductCodeWithoutTheFlagIsJudgedByItsUninstaller()
    {
        _installer.With(Product, InstallerProductState.Unknown);

        var verdict = Decide(Record(ProductKey), Uninstaller(PathPresence.Present));

        Assert.Equal(EntryStanding.Installed, verdict.Standing);
    }

    [Fact]
    public void AnMsiExecCommandNamesTheProductToAskAbout()
    {
        _installer.With(Product, InstallerProductState.Unknown);

        var verdict = Decide(Record("Tool"), new InstallerCommand("MsiExec.exe /X" + ProductKey, Product));

        Assert.Equal(EntryStanding.Stale, verdict.Standing);
    }

    [Fact]
    public void AnAbsentUninstallerWithNoInstallFolderIsStale()
    {
        var verdict = Decide(Record("Tool"), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Stale, verdict.Standing);
        Assert.Contains(@"C:\Program Files\Tool\unins000.exe", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentUninstallerAndAnAbsentInstallFolderIsStale()
    {
        var verdict = Decide(Record("Tool", ("InstallLocation", Folder + @"\")), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Stale, verdict.Standing);
        Assert.Contains($"the install folder {Folder} is gone too", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>A missing uninstaller beside a standing install folder is a broken install, not a gone one.</summary>
    [Fact]
    public void AnAbsentUninstallerBesideAStandingInstallFolderIsInstalled()
    {
        _paths.Directory(Folder);

        var verdict = Decide(Record("Tool", ("InstallLocation", Folder)), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Installed, verdict.Standing);
    }

    [Fact]
    public void ARefusedInstallFolderIsNotProofOfAbsence()
    {
        _paths.Refused(Folder);

        var verdict = Decide(Record("Tool", ("InstallLocation", Folder)), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
    }

    /// <summary>Windows answers "absent" for a path on an unplugged drive, and the program on it is not gone.</summary>
    [Fact]
    public void AnUninstallerOnADriveThatIsNotConnectedIsNotProofOfAbsence()
    {
        _paths.Disconnected(@"C:\");

        var verdict = Decide(Record("Tool"), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains("not connected", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstallFolderOnADriveThatIsNotConnectedIsNotProofOfAbsence()
    {
        _installer.With(Product, InstallerProductState.Unknown);
        _paths.Disconnected(@"G:\");

        var verdict = Decide(Record(ProductKey, ("WindowsInstaller", 1), ("InstallLocation", @"G:\Games\Tool")), MissingCommand.Instance);

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
    }

    [Fact]
    public void ARefusedUninstallerIsNotProofOfAbsence()
    {
        Assert.Equal(EntryStanding.Unproven, Decide(Record("Tool"), Uninstaller(PathPresence.Refused)).Standing);
    }

    [Fact]
    public void AQuotedInstallFolderIsReadAsAPath()
    {
        _paths.Directory(Folder);

        var verdict = Decide(Record("Tool", ("InstallLocation", $"\"{Folder}\\\"")), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Installed, verdict.Standing);
    }

    [Fact]
    public void CommandsThatNameNoUninstallerProveNothing()
    {
        Assert.Equal(EntryStanding.Unproven, Decide(Record("Tool"), MissingCommand.Instance).Standing);
        Assert.Equal(EntryStanding.Unproven, Decide(Record("Tool"), new UnparsedCommand("x")).Standing);
        Assert.Equal(EntryStanding.Unproven, Decide(Record("Tool"), new NamedCommand("winget x", "winget", "x")).Standing);
    }

    [Fact]
    public void AnUnreadableEntryProvesNothing()
    {
        var record = new UninstallRecord(new UninstallKey(UninstallScope.Machine64, "Tool"), UninstallValues.None, IsReadable: false);

        Assert.Equal(EntryStanding.Unproven, Decide(record, Uninstaller(PathPresence.Absent)).Standing);
    }

    /// <summary>
    /// "Names no install folder" is proof only where the value is missing or empty. A value that is
    /// set but cannot be checked may name a folder still standing.
    /// </summary>
    [Theory]
    [InlineData("Tool")]
    [InlineData(@"Program Files\Tool")]
    [InlineData("C:")]
    public void AnInstallFolderThatIsNotAFullPathProvesNothing(string location)
    {
        var verdict = Decide(Record("Tool", ("InstallLocation", location)), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains($"\"{location}\" is not a full path", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstallFolderThatIsNotTextProvesNothing()
    {
        var bytes = Decide(Record("Tool", ("InstallLocation", new byte[] { 67, 0 })), Uninstaller(PathPresence.Absent));
        var number = Decide(Record("Tool", ("InstallLocation", 1)), Uninstaller(PathPresence.Absent));

        Assert.Equal((EntryStanding.Unproven, EntryStanding.Unproven), (bytes.Standing, number.Standing));
        Assert.Contains("not written as text", bytes.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyInstallFolderNamesNone(string location)
    {
        var verdict = Decide(Record("Tool", ("InstallLocation", location)), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Stale, verdict.Standing);
        Assert.Contains("names no install folder", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>Some installers name the program's executable as its install location.</summary>
    [Fact]
    public void AnInstallLocationThatNamesAStandingFileIsInstalled()
    {
        _paths.File($@"{Folder}\tool.exe");

        var verdict = Decide(Record("Tool", ("InstallLocation", $@"{Folder}\tool.exe")), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Installed, verdict.Standing);
    }

    /// <summary>
    /// <c>C:\Games</c> a junction to an unplugged <c>E:\</c>: everything under it reads absent while
    /// <c>C:\</c> answers, so the drive check alone would call a program on it gone.
    /// </summary>
    [Fact]
    public void AnUninstallerThroughALinkIsNotProofOfAbsence()
    {
        _paths.Directory(@"C:\Games", isLink: true);

        var verdict = Decide(Record("Tool"), new ProgramCommand("x", @"C:\Games\Tool\unins000.exe", string.Empty, PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains(@"C:\Games is a link", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstallFolderThroughALinkIsNotProofOfAbsence()
    {
        _paths.Directory(@"C:\Games", isLink: true);

        var verdict = Decide(Record("Tool", ("InstallLocation", @"C:\Games\Tool")), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains(@"C:\Games is a link", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderOnTheWayThatWindowsWillNotDescribeIsNotProofOfAbsence()
    {
        _paths.Directory(@"C:\Program Files").Refused(@"C:\Program Files\Vendor");

        var verdict = Decide(Record("Tool", ("InstallLocation", @"C:\Program Files\Vendor\Tool")), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains(@"would not say what C:\Program Files\Vendor is", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>A walk through plain folders to a missing one is the proof the rule needs.</summary>
    [Fact]
    public void AnAbsentFolderBelowPlainFoldersIsProofOfAbsence()
    {
        _paths.Directory(@"C:\Program Files").Directory(@"C:\Program Files (x86)");

        var verdict = Decide(Record("Tool", ("InstallLocation", Folder)), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Stale, verdict.Standing);
    }

    /// <summary>
    /// A 32-bit entry's <c>%ProgramFiles%</c> expands to the 64-bit folder in a 64-bit Deguffer, so
    /// the folder it means may stand in the other one.
    /// </summary>
    [Fact]
    public void AnInstallFolderStandingInTheOtherProgramFolderIsInstalled()
    {
        _paths.Directory(@"C:\Program Files (x86)\Tool");

        var verdict = Decide(Record("Tool", ("InstallLocation", Folder)), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Installed, verdict.Standing);
    }

    [Fact]
    public void AnInstallFolderRefusedInTheOtherProgramFolderProvesNothing()
    {
        _paths.Refused(@"C:\Program Files (x86)\Tool");

        Assert.Equal(EntryStanding.Unproven, Decide(Record("Tool", ("InstallLocation", Folder)), Uninstaller(PathPresence.Absent)).Standing);
    }

    [Fact]
    public void AnUninstallerWhoseOtherViewIsThroughALinkIsNotProofOfAbsence()
    {
        _paths.Directory(@"C:\Program Files (x86)", isLink: true);

        var verdict = Decide(Record("Tool"), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains(@"C:\Program Files (x86) is a link", verdict.Reason, StringComparison.Ordinal);
    }
}
