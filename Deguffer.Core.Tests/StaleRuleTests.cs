using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;

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

    private readonly Dictionary<Guid, InstallerProductState> _products = [];

    private readonly Dictionary<string, PathPresence> _directories = new(StringComparer.OrdinalIgnoreCase);

    private StandingVerdict Decide(UninstallRecord record, UninstallCommand command) =>
        StaleRule.Decide(
            record,
            command,
            code => _products.GetValueOrDefault(code, InstallerProductState.Unanswered),
            path => _directories.GetValueOrDefault(path, PathPresence.Absent));

    private static UninstallRecord Record(string name, params (string Name, object Value)[] values) =>
        new(new UninstallKey(UninstallScope.Machine64, name), new UninstallValues(values.ToDictionary(v => v.Name, v => v.Value)));

    private static ProgramCommand Uninstaller(PathPresence presence) =>
        new($"\"{Folder}\\unins000.exe\"", $@"{Folder}\unins000.exe", string.Empty, presence);

    [Fact]
    public void AnInstallerProductWindowsInstallerDoesNotKnowIsStale()
    {
        _products[Product] = InstallerProductState.Unknown;

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
        _products[Product] = state;

        Assert.Equal(expected, Decide(Record(ProductKey, ("WindowsInstaller", 1)), MissingCommand.Instance).Standing);
    }

    /// <summary>
    /// A key named like a product code is not a Windows Installer entry by its name alone: the
    /// measured machine had 45 that were not. Without the flag the uninstaller is what is asked.
    /// </summary>
    [Fact]
    public void AKeyNamedLikeAProductCodeWithoutTheFlagIsJudgedByItsUninstaller()
    {
        _products[Product] = InstallerProductState.Unknown;

        var verdict = Decide(Record(ProductKey), Uninstaller(PathPresence.Present));

        Assert.Equal(EntryStanding.Installed, verdict.Standing);
    }

    [Fact]
    public void AnMsiExecCommandNamesTheProductToAskAbout()
    {
        _products[Product] = InstallerProductState.Unknown;

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
        _directories[Folder] = PathPresence.Present;

        var verdict = Decide(Record("Tool", ("InstallLocation", Folder)), Uninstaller(PathPresence.Absent));

        Assert.Equal(EntryStanding.Installed, verdict.Standing);
    }

    [Fact]
    public void ARefusedInstallFolderIsNotProofOfAbsence()
    {
        _directories[Folder] = PathPresence.Refused;

        var verdict = Decide(Record("Tool", ("InstallLocation", Folder)), Uninstaller(PathPresence.Absent));

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
        _directories[Folder] = PathPresence.Present;

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
}
