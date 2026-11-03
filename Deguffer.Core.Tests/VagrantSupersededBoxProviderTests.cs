using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using static Deguffer.Core.Tests.VagrantHome;

namespace Deguffer.Core.Tests;

/// <summary>
/// Vagrant's superseded box versions, offered through Vagrant's own prune (§5.1). The subject is a
/// home holding several versions of a box beside its newest, which is what an update leaves; the
/// negative assertions carry the weight, because the newest version, the home's own records and every
/// machine's box must all outlive the run.
/// </summary>
public sealed class VagrantSupersededBoxProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public VagrantSupersededBoxProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("vagrant");
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private string Vagrant => _environment.FindExecutable("vagrant")!;

    private string DefaultHome => Path.Combine(_environment.UserProfile, ".vagrant.d");

    private VagrantSupersededBoxProvider CreateProvider(FakeProcessRunner runner, IProcessInspector? inspector = null) =>
        new(_environment, runner, inspector ?? FakeProcessInspector.NothingRunning, system: _system);

    private FakeProcessRunner Reporting(string report, int exitCode = 0) =>
        new FakeProcessRunner().Responding(Vagrant, "--dry-run", report, exitCode);

    private static RunCommandStep TheStep(CleanupPlan plan) => Assert.Single(plan.Steps.OfType<RunCommandStep>());

    [Fact]
    public async Task ReportsNotPresentAndRunsNothingWhenVagrantIsNotInstalled()
    {
        var environment = new FakeUserEnvironment(Path.Combine(_temp.Path, "bare"));
        var runner = new FakeProcessRunner();
        var provider = new VagrantSupersededBoxProvider(environment, runner, FakeProcessInspector.NothingRunning, system: _system);

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Empty(runner.Invocations);
    }

    /// <summary>The tier is the product (§3), and §7 derives the confirmation from it.</summary>
    [Fact]
    public async Task IsTier2AndCarriesThatIntoThePlan()
    {
        InstallBionic(DefaultHome);
        var provider = CreateProvider(Reporting(BionicReport));

        Assert.Equal(SafetyTier.RegenerableWithCost, provider.Tier);
        Assert.Equal(SafetyTier.RegenerableWithCost, (await provider.PlanAsync()).Tier);
    }

    /// <summary>
    /// §5.1: the dry run is asked exactly as written, as a query, and the step is prune with the two
    /// flags that never prompt, run by the program found on PATH and left to finish if cancelled.
    /// </summary>
    [Fact]
    public async Task AsksWithTheDryRunAndPrunesWithVagrantsOwnCommand()
    {
        InstallBionic(DefaultHome);
        var runner = Reporting(BionicReport);

        var plan = await CreateProvider(runner).PlanAsync();

        var asked = Assert.Single(runner.Invocations);
        Assert.Equal(Vagrant, asked.FileName);
        Assert.Equal("box prune --dry-run", asked.Arguments);
        Assert.Same(ToolRun.Query, asked.Run);

        var step = TheStep(plan);
        Assert.Equal(Vagrant, step.FileName);
        Assert.Equal("box prune --force --keep-active-boxes", step.Arguments);
        Assert.Equal(CancelledCommand.Finishes, step.WhenCancelled);
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// The figure is the provider folders of the versions prune names and nothing else: the newest
    /// version is beside them and is never counted.
    /// </summary>
    [Fact]
    public async Task MeasuresExactlyTheVersionsPruneWouldRemoveInTheDefaultHome()
    {
        var (kept, removed) = InstallBionic(DefaultHome);

        var step = TheStep(await CreateProvider(Reporting(BionicReport)).PlanAsync());

        Assert.Equal(removed.Order(StringComparer.OrdinalIgnoreCase), step.MeasuredPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(kept, step.MeasuredPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3000 + (2 * "{\"provider\":\"virtualbox\"}".Length), step.EstimatedBytes);
    }

    [Fact]
    public async Task ReadsTheHomeFromVagrantHomeWhenItIsSet()
    {
        var home = Path.Combine(_temp.Path, "elsewhere", "vagrant-home");
        _environment.WithEnvironmentVariable(VagrantSupersededBoxProvider.HomeVariable, home);
        var (_, removed) = InstallBionic(home);
        InstallBionic(DefaultHome);

        var plan = await CreateProvider(Reporting(BionicReport)).PlanAsync();

        Assert.Equal(removed.Order(StringComparer.OrdinalIgnoreCase), TheStep(plan).MeasuredPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(home, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Prune acts on the home its own environment names, so a home Deguffer started with and the machine
    /// has since dropped is the one examined, measured and asserted as surviving (§5.6), never the default.
    /// </summary>
    [Fact]
    public async Task ExaminesTheHomeThePruneWillActOn()
    {
        var home = Path.Combine(_temp.Path, "elsewhere", "vagrant-home");
        _environment.WithToolEnvironmentVariable(VagrantSupersededBoxProvider.HomeVariable, home);
        var (kept, removed) = InstallBionic(home);
        InstallBionic(DefaultHome);
        var provider = CreateProvider(Reporting(BionicReport));

        var plan = await provider.PlanAsync();

        Assert.Equal(removed.Order(StringComparer.OrdinalIgnoreCase), TheStep(plan).MeasuredPaths.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var survivor in new[] { home, BoxesIn(home), kept })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        Assert.Contains(provider.ToolRoots, root => root.Path.Equals(home, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(await provider.CleanedPlacesAsync(), place => place.Path.Equals(BoxesIn(home), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A home declined as prune would read it is declined, though the machine's own value is unset.</summary>
    [Fact]
    public async Task RunsNothingWhereThePruneWouldReadTheProfileAsVagrantsHome()
    {
        _environment.WithToolEnvironmentVariable(VagrantSupersededBoxProvider.HomeVariable, _environment.UserProfile);
        InstallBionic(_environment.UserProfile);
        InstallBionic(DefaultHome);
        var runner = Reporting(BionicReport);
        var provider = CreateProvider(runner);

        var plan = await provider.PlanAsync();

        Assert.True(plan.WasNotExamined);
        Assert.Empty(runner.Invocations);
        Assert.Equal(_environment.UserProfile, Assert.Single(await provider.DiscoverToolRootsAsync()).Path);
    }

    /// <summary>
    /// Vagrant expands a relative value against its own working directory, which is Deguffer's tool
    /// folder rather than anything the user chose, so there is no reading of it to act on.
    /// </summary>
    [Fact]
    public async Task RunsNothingWhenVagrantHomeIsNotAFullPath()
    {
        InstallBionic(DefaultHome);
        _environment.WithEnvironmentVariable(VagrantSupersededBoxProvider.HomeVariable, @"~\.vagrant.d");
        var runner = Reporting(BionicReport);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Empty(runner.Invocations);
        Assert.Contains(plan.Notes, n => n.Message.Contains("not a full path", StringComparison.Ordinal));
    }

    /// <summary>
    /// The variable naming the profile would assert everything there as Vagrant's survivor, so the
    /// home is declined; Explore still refuses what Vagrant writes there, and nothing else.
    /// </summary>
    [Fact]
    public async Task WillNotTreatTheProfileAsVagrantsHome()
    {
        _environment.WithEnvironmentVariable(VagrantSupersededBoxProvider.HomeVariable, _environment.UserProfile);
        Install(_environment.UserProfile, "hashicorp-VAGRANTSLASH-bionic64", "1.0.0", "virtualbox");
        var runner = Reporting(BionicReport);
        var provider = CreateProvider(runner);

        var plan = await provider.PlanAsync();
        var roots = await provider.DiscoverToolRootsAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Empty(runner.Invocations);

        var declined = Assert.Single(roots);
        Assert.Equal(_environment.UserProfile, declined.Path);
        Assert.Equal(ToolRootClaim.NamedEntries, declined.Claim);
        Assert.False(declined.Recognises(new ToolRootChild("boxes", ChildKind.Folder)));
        Assert.False(declined.Recognises(new ToolRootChild("insecure_private_key", ChildKind.File)));
        Assert.True(declined.Recognises(new ToolRootChild("Projects", ChildKind.Folder)));
    }

    /// <summary>
    /// A version prune names that is not where Vagrant keeps a box is not counted, and the plan says so
    /// rather than reporting a figure for it.
    /// </summary>
    [Fact]
    public async Task CountsOnlyTheVersionsFoundWhereVagrantKeepsThem()
    {
        Install(DefaultHome, "hashicorp-VAGRANTSLASH-bionic64", "1.0.282", "virtualbox");
        var found = Install(DefaultHome, "hashicorp-VAGRANTSLASH-bionic64", "1.0.1", "virtualbox");

        var plan = await CreateProvider(Reporting(BionicReport)).PlanAsync();

        Assert.Equal([found], TheStep(plan).MeasuredPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("hashicorp/bionic64 1.0.0", StringComparison.Ordinal)
            && n.Message.Contains("not counted", StringComparison.Ordinal));
    }

    /// <summary>Any Vagrant command creates a home that is missing, so none is started without one.</summary>
    [Fact]
    public async Task NeverStartsVagrantWhereTheHomeHasNoBoxes()
    {
        Directory.CreateDirectory(DefaultHome);
        var runner = Reporting(BionicReport);

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.False(plan.HasUnreadableRoot);
        Assert.Empty(runner.Invocations);
        Assert.False(Directory.Exists(BoxesIn(DefaultHome)));
    }

    [Fact]
    public async Task SaysTheBoxesCouldNotBeReachedRatherThanThatThereAreNone()
    {
        InstallBionic(DefaultHome);
        var runner = Reporting(BionicReport);

        using (DeniedDirectory.WithUnreadableAttributes(BoxesIn(DefaultHome)))
        {
            Assert.Equal(PathPresence.Refused, LongPath.ProbeDirectory(BoxesIn(DefaultHome)));

            var plan = await CreateProvider(runner).PlanAsync();

            Assert.True(plan.HasUnreadableRoot);
            Assert.True(plan.IsEmpty);
            Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(BoxesIn(DefaultHome), StringComparison.OrdinalIgnoreCase));
        }

        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task OffersNothingAndSaysWhyWhenTheDryRunFails()
    {
        InstallBionic(DefaultHome);
        var runner = new FakeProcessRunner().Replying(Vagrant, _ =>
            new CommandOutcome(1, string.Empty, "There was an error loading a Vagrantfile."));

        var plan = await CreateProvider(runner).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("There was an error loading a Vagrantfile.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OffersNothingWhenTheDryRunIsNotAReport()
    {
        InstallBionic(DefaultHome);

        var plan = await CreateProvider(Reporting("Usage: vagrant [options] <command> [<args>]\n")).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
    }

    [Theory]
    [InlineData("There are no installed boxes! Use `vagrant box add` to add some.\n")]
    [InlineData("The following boxes will be kept...\nhashicorp/bionic64 (virtualbox, 1.0.282)\n\nChecking for older boxes...\nNo old versions of boxes to remove...\n")]
    public async Task IsClearWhereVagrantNamesNothingToRemove(string report)
    {
        Install(DefaultHome, "hashicorp-VAGRANTSLASH-bionic64", "1.0.282", "virtualbox");

        var plan = await CreateProvider(Reporting(report)).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.False(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("no superseded box versions", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>BoxCollection#dir_name</c> maps a colon to <c>-VAGRANTCOLON-</c> on Windows, then a slash to
    /// <c>-VAGRANTSLASH-</c>.
    /// </summary>
    [Fact]
    public async Task FindsABoxWhoseNameHoldsASlashAndAColon()
    {
        Install(DefaultHome, "registry.test-VAGRANTCOLON-443-VAGRANTSLASH-team-VAGRANTSLASH-base", "2.0", "libvirt");
        var removed = Install(DefaultHome, "registry.test-VAGRANTCOLON-443-VAGRANTSLASH-team-VAGRANTSLASH-base", "1.0", "libvirt");

        var plan = await CreateProvider(Reporting(Report(
            ["registry.test:443/team/base (libvirt, 2.0)"],
            ["registry.test:443/team/base libvirt 1.0"]))).PlanAsync();

        Assert.Equal([removed], TheStep(plan).MeasuredPaths);
    }

    /// <summary>
    /// The remove action takes the folder for this machine's architecture, and <c>BoxCollection#all</c>
    /// hides the older layout's folder of the same version behind it, so that one stays and is asserted to.
    /// </summary>
    [Fact]
    public async Task MeasuresTheArchitectureFolderAndProtectsTheOlderLayoutBesideIt()
    {
        Install(DefaultHome, "generic-VAGRANTSLASH-alpine38", "4.3.12", "hyperv", "amd64");
        var removed = Install(DefaultHome, "generic-VAGRANTSLASH-alpine38", "4.0.0", "hyperv", "amd64");
        var hidden = Install(DefaultHome, "generic-VAGRANTSLASH-alpine38", "4.0.0", "hyperv");
        var otherProvider = Install(DefaultHome, "generic-VAGRANTSLASH-alpine38", "4.0.0", "libvirt", "amd64");

        var plan = await CreateProvider(Reporting(Report(
            ["generic/alpine38 (hyperv, 4.3.12)", "generic/alpine38 (libvirt, 4.0.0)"],
            ["generic/alpine38 hyperv 4.0.0"]))).PlanAsync();

        Assert.Equal([removed], TheStep(plan).MeasuredPaths);

        foreach (var survivor in new[] { hidden, otherProvider })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }
    }

    /// <summary>
    /// Prune names no architecture, so the remove action raises for a version installed for two, and
    /// the run ends part-way with an error. The dry run lists such a version once for each: here the
    /// older layout's folder beside another architecture's, which the folders alone do not show as two.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhereAVersionIsInstalledForTwoArchitectures()
    {
        Install(DefaultHome, "generic-VAGRANTSLASH-alpine38", "4.3.12", "libvirt", "amd64");
        Install(DefaultHome, "generic-VAGRANTSLASH-alpine38", "4.0.0", "libvirt");
        Install(DefaultHome, "generic-VAGRANTSLASH-alpine38", "4.0.0", "libvirt", "arm64");

        var plan = await CreateProvider(Reporting(Report(
            ["generic/alpine38 (libvirt, 4.3.12)"],
            ["generic/alpine38 libvirt 4.0.0", "generic/alpine38 libvirt 4.0.0"]))).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("--architecture", StringComparison.Ordinal));
    }

    /// <summary>The same version on two architectures where the dry run, read earlier, named it once.</summary>
    [Fact]
    public async Task OffersNothingWhereTheFoldersShowTwoArchitecturesForAVersionNamedOnce()
    {
        Install(DefaultHome, "generic-VAGRANTSLASH-alpine38", "4.3.12", "libvirt", "amd64");
        Install(DefaultHome, "generic-VAGRANTSLASH-alpine38", "4.0.0", "libvirt", "amd64");
        Install(DefaultHome, "generic-VAGRANTSLASH-alpine38", "4.0.0", "libvirt", "arm64");

        var plan = await CreateProvider(Reporting(Report(
            ["generic/alpine38 (libvirt, 4.3.12)"],
            ["generic/alpine38 libvirt 4.0.0"]))).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("--architecture", StringComparison.Ordinal));
    }

    /// <summary>
    /// A machine recorded as created from a version prune would remove withholds the row, even though
    /// its recorded architecture is <c>auto</c>: Vagrant's own check compares that and would not keep it.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhileAMachineWasCreatedFromAVersionPruneWouldRemove()
    {
        InstallBionic(DefaultHome);
        WriteIndex(DefaultHome, ("default", "hashicorp/bionic64", "virtualbox", "1.0.1"));

        var plan = await CreateProvider(Reporting(BionicReport)).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("'default'", StringComparison.Ordinal)
            && n.Message.Contains("hashicorp/bionic64 1.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StillOffersThePruneWhereMachinesUseOnlyTheNewestVersions()
    {
        InstallBionic(DefaultHome);
        WriteIndex(
            DefaultHome,
            ("default", "hashicorp/bionic64", "virtualbox", "1.0.282"),
            ("other", "hashicorp/bionic64", "libvirt", "1.0.1"));

        var plan = await CreateProvider(Reporting(BionicReport)).PlanAsync();

        Assert.Equal(2, TheStep(plan).MeasuredPaths.Count);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"version": 2, "machines": {}}""")]
    public async Task OffersNothingWhenTheMachineIndexCannotBeRead(string content)
    {
        InstallBionic(DefaultHome);
        var index = Path.Combine(DefaultHome, "data", "machine-index", "index");
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        File.WriteAllText(index, content);

        var plan = await CreateProvider(Reporting(BionicReport)).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(index, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OffersNothingWhenWindowsWillNotSayWhetherTheMachineIndexIsThere()
    {
        InstallBionic(DefaultHome);
        WriteIndex(DefaultHome);
        var index = Path.Combine(DefaultHome, "data", "machine-index", "index");

        using var denied = DeniedDirectory.WithUnreadableFile(index);
        Assert.Equal(PathPresence.Refused, LongPath.ProbeFile(index));

        var plan = await CreateProvider(Reporting(BionicReport)).PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("Windows would not say", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6: the home, what Vagrant keeps in it, every box folder and the newest version all survive,
    /// and so does a version folder prune never names.
    /// </summary>
    [Fact]
    public async Task ProtectsTheHomeItsRecordsAndTheNewestVersion()
    {
        var (kept, _) = InstallBionic(DefaultHome);
        var single = Install(DefaultHome, "generic-VAGRANTSLASH-alpine38", "4.3.12", "hyperv");
        var malformed = Path.Combine(BoxesIn(DefaultHome), "hashicorp-VAGRANTSLASH-bionic64", "latest");
        Directory.CreateDirectory(malformed);

        string[] records = ["Vagrantfile", "insecure_private_key", "plugins.json", "aliases"];
        foreach (var record in records)
        {
            File.WriteAllText(Path.Combine(DefaultHome, record), "record");
        }

        string[] folders = [Path.Combine("data", "machine-index"), "insecure_private_keys", "gems"];
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(DefaultHome, folder));
        }

        var plan = await CreateProvider(Reporting(Report(
            ["hashicorp/bionic64 (virtualbox, 1.0.282)", "generic/alpine38   (hyperv, 4.3.12)"],
            ["hashicorp/bionic64 virtualbox 1.0.0", "hashicorp/bionic64 virtualbox 1.0.1"]))).PlanAsync();

        string[] survivors =
        [
            DefaultHome,
            BoxesIn(DefaultHome),
            kept,
            Path.GetDirectoryName(kept)!,
            Path.GetDirectoryName(Path.GetDirectoryName(single))!,
            malformed,
            .. records.Concat(folders).Select(name => Path.Combine(DefaultHome, name)),
        ];

        foreach (var survivor in survivors)
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(survivor, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        Assert.DoesNotContain(TheStep(plan).MeasuredPaths, path => survivors.Contains(path, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerificationFailsIfTheNewestVersionVanished()
    {
        var (kept, _) = InstallBionic(DefaultHome);
        var provider = CreateProvider(Reporting(BionicReport));
        var plan = await provider.PlanAsync();

        Directory.Delete(kept, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, failure => failure.Subject.Equals(kept, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>§5.2 as §7.1 reads it: nothing in the home is a path target, the boxes included.</summary>
    [Fact]
    public async Task ExploreRefusesEverythingInTheHome()
    {
        var (kept, removed) = InstallBionic(DefaultHome);
        var provider = CreateProvider(Reporting(BionicReport));

        var policy = await ExploreActionPolicy.ForAsync(_system, _environment, new FakeVolumeInventory(), [provider]);

        foreach (var path in new[] { DefaultHome, BoxesIn(DefaultHome), kept, removed[0], Path.Combine(DefaultHome, "tmp") })
        {
            Assert.False(policy.MayRemove(path).IsAllowed, path);
        }
    }

    [Fact]
    public async Task NamesTheBoxesOfBothHomesAsWhereItsCleanDeletes()
    {
        var home = Path.Combine(_temp.Path, "elsewhere", "vagrant-home");
        _environment.WithEnvironmentVariable(VagrantSupersededBoxProvider.HomeVariable, home);

        var places = await CreateProvider(new FakeProcessRunner()).CleanedPlacesAsync();

        Assert.Equal(
            new[] { BoxesIn(home), BoxesIn(DefaultHome) }.Order(StringComparer.OrdinalIgnoreCase),
            places.Select(place => place.Path).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WarnsWhenVagrantIsRunning()
    {
        InstallBionic(DefaultHome);

        var plan = await CreateProvider(Reporting(BionicReport), new FakeProcessInspector("vagrant")).PlanAsync();

        Assert.Single(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }
}
