using Deguffer.Core.InstalledApps;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.3's rule for a WiX Burn bundle: it is judged by the Windows Installer packages it installed,
/// and it is stale only when every package that names it is a product Windows Installer no longer
/// knows. A bundle whose cached setup a cleaner removed still stands for the programs it installed.
/// </summary>
public sealed class BundleRuleTests
{
    private const string Bundle = "{0A1B2C3D-0000-0000-0000-000000000001}";

    private const string OtherBundle = "{0A1B2C3D-0000-0000-0000-000000000002}";

    private const string CoreCode = "{11111111-1111-1111-1111-111111111111}";

    private const string ExtrasCode = "{22222222-2222-2222-2222-222222222222}";

    private const string CachedSetup = $@"C:\ProgramData\Package Cache\{Bundle}\ToolSetup.exe";

    private static readonly Guid Core = Guid.Parse(CoreCode);

    private static readonly Guid Extras = Guid.Parse(ExtrasCode);

    private readonly FakeWindowsInstaller _installer = new();

    private readonly FakePathProbe _paths = new();

    private readonly FakePackageDependencies _dependencies = new();

    private readonly FakeUninstallRegistry _registry = new();

    /// <summary>Asked through the reader's own per-reading answers, so the entry-name check is the one it runs.</summary>
    private StandingVerdict Decide(UninstallRecord record, UninstallCommand command)
    {
        var answers = new ReadingAnswers(_registry, _installer, _paths, _dependencies);
        var evidence = new StaleEvidence(
            answers, new InstalledPaths(answers, FixedSystemDirectories.Standard, new FakeVolumeInventory()), answers, answers.NonInstallerEntryNamed);

        return StaleRule.Decide(record, command, evidence);
    }

    private static UninstallRecord BundleEntry(params (string Name, object Value)[] values) =>
        new(new UninstallKey(UninstallScope.Machine32, Bundle), new UninstallValues(values.ToDictionary(v => v.Name, v => v.Value)));

    private static UninstallRecord Registered() => BundleEntry(("DisplayName", "Tool"), ("BundleProviderKey", Bundle));

    private static ProgramCommand Setup(PathPresence presence) =>
        new($"\"{CachedSetup}\" /uninstall", CachedSetup, "/uninstall", presence);

    private FakePackageDependencies BothPackages() =>
        _dependencies.Provider("Tool.Core", CoreCode, Bundle).Provider("Tool.Extras", ExtrasCode, Bundle);

    [Fact]
    public void ABundleWhosePackagesAndSetupAreGoneIsStale()
    {
        BothPackages();

        var verdict = Decide(Registered(), Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Stale, verdict.Standing);
        Assert.Equal(
            $"Windows Installer knows none of the 2 products this bundle installed, and the uninstaller {CachedSetup} is gone, and the entry names no install folder.",
            verdict.Reason);
    }

    /// <summary>The case the rule exists for: the cache was emptied and the program is still installed.</summary>
    [Fact]
    public void ABundleWithOneProductStillInstalledIsInstalled()
    {
        BothPackages();
        _installer.With(Extras, InstallerProductState.Installed);

        var verdict = Decide(Registered(), Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Installed, verdict.Standing);
        Assert.Contains(ExtrasCode, verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InstallerProductState.Advertised, EntryStanding.Installed)]
    [InlineData(InstallerProductState.OtherAccount, EntryStanding.Installed)]
    [InlineData(InstallerProductState.Unanswered, EntryStanding.Unproven)]
    public void AnyOtherInstallerAnswerForAPackageKeepsTheBundle(InstallerProductState state, EntryStanding expected)
    {
        BothPackages();
        _installer.With(Core, state);

        Assert.Equal(expected, Decide(Registered(), Setup(PathPresence.Absent)).Standing);
    }

    [Fact]
    public void ABundleNoPackageNamesProvesNothing()
    {
        var verdict = Decide(Registered(), Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains("No package registration names this bundle", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>An executable package's provider has no default value, and there is nothing to ask about.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Tool.Runtime")]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    public void APackageThatNamesNoProductCodeProvesNothing(string? code)
    {
        _dependencies.Provider("Tool.Core", CoreCode, Bundle).Provider("Tool.Runtime", code, Bundle);

        var verdict = Decide(Registered(), Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains("Tool.Runtime names no product code", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Older WiX packages register their provider under their product code with no default value,
    /// as the measured Unreal Engine prerequisite bundles do. A key Windows Installer knows is the
    /// package standing.
    /// </summary>
    [Theory]
    [InlineData(InstallerProductState.Installed)]
    [InlineData(InstallerProductState.Advertised)]
    [InlineData(InstallerProductState.OtherAccount)]
    public void AProviderNamedByAProductWindowsInstallerKnowsKeepsTheBundle(InstallerProductState state)
    {
        _dependencies.Provider(CoreCode, null, Bundle);
        _installer.With(Core, state);

        var verdict = Decide(Registered(), Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Installed, verdict.Standing);
        Assert.Contains(CoreCode, verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key Windows Installer does not know may be an executable package's, so it never counts as
    /// a product gone, even beside packages that are.
    /// </summary>
    [Theory]
    [InlineData(InstallerProductState.Unknown)]
    [InlineData(InstallerProductState.Unanswered)]
    public void AProviderNamedByACodeWindowsInstallerDoesNotKnowProvesNothing(InstallerProductState state)
    {
        _dependencies.Provider("Tool.Extras", ExtrasCode, Bundle).Provider(CoreCode, null, Bundle);
        _installer.With(Core, state);

        var verdict = Decide(Registered(), Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains($"{CoreCode} names no product code", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two of the measured prerequisite bundles named a key Windows Installer did not know, and
    /// their cached setup was there. The setup standing is proof, and the row says so.
    /// </summary>
    [Fact]
    public void ABundleWhosePackagesCannotBeJudgedButWhoseSetupStandsIsInstalled()
    {
        _dependencies.Provider(CoreCode, null, Bundle);

        var verdict = Decide(Registered(), Setup(PathPresence.Present));

        Assert.Equal(EntryStanding.Installed, verdict.Standing);
        Assert.Contains($"The uninstaller {CachedSetup} is there", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>One package still installed decides it, whatever another package's registration lacks.</summary>
    [Fact]
    public void AnInstalledPackageOutweighsOneThatNamesNoCode()
    {
        _dependencies.Provider("Tool.Runtime", null, Bundle).Provider("Tool.Core", CoreCode, Bundle);
        _installer.With(Core, InstallerProductState.Installed);

        Assert.Equal(EntryStanding.Installed, Decide(Registered(), Setup(PathPresence.Absent)).Standing);
    }

    /// <summary>A patch code is unknown to <c>MsiQueryProductState</c> whether or not the patch is there.</summary>
    [Fact]
    public void APatchWindowsInstallerStillHoldsKeepsTheBundle()
    {
        BothPackages();
        _installer.Patches = new InstallerPatches(new HashSet<Guid> { Extras }, IsComplete: true);

        var verdict = Decide(Registered(), Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Installed, verdict.Standing);
        Assert.Contains($"patch {ExtrasCode}", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCodeProvesNothingWhereThePatchesWereNotAllListed()
    {
        BothPackages();
        _installer.Patches = new InstallerPatches(new HashSet<Guid>(), IsComplete: false);

        Assert.Equal(EntryStanding.Unproven, Decide(Registered(), Setup(PathPresence.Absent)).Standing);
    }

    /// <summary>An addon bundle is listed by its parent bundle's provider, whose code is the parent's entry name.</summary>
    [Fact]
    public void ACodeThatNamesAnotherBundlesEntryProvesNothing()
    {
        _dependencies.Provider("Tool.Core", CoreCode, Bundle).Provider("Parent.Bundle", OtherBundle, Bundle);
        _registry.With(UninstallScope.Machine32, OtherBundle, ("DisplayName", "Parent"), ("BundleProviderKey", "Parent.Bundle"));

        var verdict = Decide(Registered(), Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains($"names {OtherBundle.ToUpperInvariant()}, which is another entry", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>A product's own entry left behind names the code as a product code, which is what the rule asks for.</summary>
    [Fact]
    public void ACodeWhoseOwnInstallerEntryIsLeftBehindIsStillAProductCode()
    {
        BothPackages();
        _registry.With(UninstallScope.Machine32, CoreCode, ("DisplayName", "Tool Core"), ("WindowsInstaller", 1));

        Assert.Equal(EntryStanding.Stale, Decide(Registered(), Setup(PathPresence.Absent)).Standing);
    }

    [Fact]
    public void RegistrationsWindowsWouldNotAllShowProveNothing()
    {
        BothPackages().Refusing();

        var verdict = Decide(Registered(), Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains("would not show every package registration", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A shell marked only by the packages naming it cannot be told apart from any other entry
    /// where a registration was hidden, so its missing uninstaller proves nothing.
    /// </summary>
    [Fact]
    public void AnEntryWhoseRegistrationsWindowsWouldNotAllShowIsNotProvenByItsUninstaller()
    {
        _dependencies.Refusing();

        var verdict = Decide(BundleEntry(("DisplayName", "Tool")), Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains("would not show every package registration", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>Each value Burn writes marks a bundle alone, with no package naming it.</summary>
    [Theory]
    [InlineData("BundleProviderKey")]
    [InlineData("BundleCachePath")]
    [InlineData("BundleUpgradeCode")]
    [InlineData("BundleVersion")]
    [InlineData("EngineVersion")]
    public void EachBurnValueMarksABundle(string name)
    {
        var verdict = Decide(BundleEntry(("DisplayName", "Tool"), (name, "x")), Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains("No package registration names this bundle", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryNameWindowsWouldNotLookUpProvesNothing()
    {
        BothPackages();
        _registry.Refusing(UninstallScope.CurrentUser);

        var verdict = Decide(Registered(), Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains("would not say whether an entry is named", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bundle registers a provider of its own, which names the bundle rather than a package. Both
    /// the key its entry names and a provider whose code is the bundle's own are left out.
    /// </summary>
    [Fact]
    public void TheBundlesOwnProviderIsNotOneOfItsPackages()
    {
        _dependencies.Provider("Tool.Bundle", "{99999999-9999-9999-9999-999999999999}", Bundle).Provider("Unnamed", Bundle, Bundle);

        var verdict = Decide(
            BundleEntry(("DisplayName", "Tool"), ("BundleProviderKey", "Tool.Bundle")),
            Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Unproven, verdict.Standing);
        Assert.Contains("No package registration names this bundle", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>A bundle that can still run its own uninstaller is one Windows can remove.</summary>
    [Fact]
    public void ABundleWhoseSetupStandsIsInstalledEvenWithItsPackagesGone()
    {
        BothPackages();

        Assert.Equal(EntryStanding.Installed, Decide(Registered(), Setup(PathPresence.Present)).Standing);
    }

    [Fact]
    public void ABundleWhoseSetupIsOnADriveThatIsNotConnectedProvesNothing()
    {
        BothPackages();
        _paths.Disconnected(@"C:\");

        Assert.Equal(EntryStanding.Unproven, Decide(Registered(), Setup(PathPresence.Absent)).Standing);
    }

    /// <summary>
    /// The uninstaller alone would call this bundle stale: before the rule, a bundle whose cache was
    /// emptied read as gone while its program was installed.
    /// </summary>
    [Fact]
    public void ABundleMarkedOnlyByItsValuesIsStillJudgedByItsPackages()
    {
        _dependencies.Provider("Tool.Core", CoreCode, Bundle);
        _installer.With(Core, InstallerProductState.Installed);

        var verdict = Decide(BundleEntry(("DisplayName", "Tool"), ("BundleCachePath", CachedSetup)), Setup(PathPresence.Absent));

        Assert.Equal(EntryStanding.Installed, verdict.Standing);
    }

    /// <summary>A shell Burn left behind carries only <c>Resume</c> and <c>Installed</c>: the packages naming it mark it.</summary>
    [Fact]
    public void AShellNamedOnlyByItsPackagesIsJudgedByThem()
    {
        BothPackages();

        var verdict = Decide(BundleEntry(("Resume", 1), ("Installed", 1)), MissingCommand.Instance);

        Assert.Equal(EntryStanding.Stale, verdict.Standing);
        Assert.StartsWith("Windows Installer knows none of the 2 products this bundle installed, and the entry names", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodeListedTwiceIsCountedOnce()
    {
        _dependencies.Provider("Tool.Core", CoreCode, Bundle).Provider("Tool.Core.Again", CoreCode.ToLowerInvariant(), Bundle);

        var verdict = Decide(Registered(), MissingCommand.Instance);

        Assert.StartsWith("Windows Installer does not know the product this bundle installed", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ABundleWhoseInstallFolderStandsIsInstalled()
    {
        BothPackages();
        _paths.Directory(@"C:\Program Files\Tool");

        var verdict = Decide(BundleEntry(("BundleProviderKey", Bundle), ("InstallLocation", @"C:\Program Files\Tool")), MissingCommand.Instance);

        Assert.Equal(EntryStanding.Installed, verdict.Standing);
    }

    /// <summary>A Windows Installer entry a bundle lists is still judged by its own product code.</summary>
    [Fact]
    public void AnInstallerEntryIsJudgedByItsProductEvenWhereAPackageNamesIt()
    {
        _dependencies.Provider("Tool.Core", ExtrasCode, CoreCode);
        _installer.With(Core, InstallerProductState.Installed);
        var record = new UninstallRecord(
            new UninstallKey(UninstallScope.Machine64, CoreCode),
            new UninstallValues(new Dictionary<string, object> { ["WindowsInstaller"] = 1 }));

        Assert.Equal(EntryStanding.Installed, Decide(record, MissingCommand.Instance).Standing);
    }
}
