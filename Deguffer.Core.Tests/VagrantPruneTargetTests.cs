using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using static Deguffer.Core.Tests.VagrantHome;

namespace Deguffer.Core.Tests;

/// <summary>
/// The check asked immediately before Vagrant's prune runs. Prune ranks the installed versions again
/// when it runs, so what changes between the preview and the clean decides what it removes: each test
/// plans, changes the home the way Vagrant itself would, and asks the step whether it may still run.
/// </summary>
public sealed class VagrantPruneTargetTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;

    public VagrantPruneTargetTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("vagrant");
        _system = new FakeSystemDirectories(Path.Combine(_temp.Path, "system"));
    }

    public void Dispose() => _temp.Dispose();

    private string Home => Path.Combine(_environment.UserProfile, ".vagrant.d");

    private string IndexPath => Path.Combine(Home, "data", "machine-index", "index");

    private FakeProcessRunner Runner => new FakeProcessRunner()
        .Responding(_environment.FindExecutable("vagrant")!, "--dry-run", BionicReport, 0);

    /// <summary>
    /// bionic64 planned with a machine on its newest version, which is the state in which the prune is
    /// offered at all, and the step that plan holds.
    /// </summary>
    private async Task<RunCommandStep> PlannedPrune()
    {
        InstallBionic(Home);
        WriteIndex(Home, ("default", "hashicorp/bionic64", "virtualbox", "1.0.282"));

        var plan = await new VagrantSupersededBoxProvider(_environment, Runner, FakeProcessInspector.NothingRunning, system: _system)
            .PlanAsync();

        return Assert.Single(plan.Steps.OfType<RunCommandStep>());
    }

    private static string? WhyNot(RunCommandStep step) =>
        Assert.IsType<VagrantPruneTarget>(step.TargetCheck).WhyNot(step, CancellationToken.None);

    [Fact]
    public async Task RunsWhileTheBoxesAndMachinesAreAsThePlanSawThem()
    {
        var step = await PlannedPrune();

        Assert.Null(WhyNot(step));
    }

    /// <summary>
    /// <c>vagrant box update</c> after the preview: 1.0.282 is no longer the newest, so prune would
    /// remove it, and the machine created from it with it.
    /// </summary>
    [Fact]
    public async Task RefusesOnceANewerVersionIsInstalledAfterThePlan()
    {
        var step = await PlannedPrune();

        Install(Home, "hashicorp-VAGRANTSLASH-bionic64", "1.0.300", "virtualbox");

        Assert.Contains("have changed since the preview", WhyNot(step), StringComparison.Ordinal);
    }

    /// <summary>
    /// A version prune removes, installed again for a second architecture after the preview, which
    /// would stop prune with an error part-way through.
    /// </summary>
    [Fact]
    public async Task RefusesOnceARemovedVersionIsInstalledForAnotherArchitecture()
    {
        var step = await PlannedPrune();

        Install(Home, "hashicorp-VAGRANTSLASH-bionic64", "1.0.0", "virtualbox", architecture: "arm64");

        Assert.Contains("have changed since the preview", WhyNot(step), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>vagrant up</c> of a project pinned to 1.0.0 after the preview: a machine is now created from a
    /// version prune removes, which <c>Box#in_use?</c> would not recognise.
    /// </summary>
    [Fact]
    public async Task RefusesOnceAMachineIsCreatedFromAVersionPruneRemoves()
    {
        var step = await PlannedPrune();

        WriteIndex(
            Home,
            ("default", "hashicorp/bionic64", "virtualbox", "1.0.282"),
            ("pinned", "hashicorp/bionic64", "virtualbox", "1.0.0"));

        var why = WhyNot(step);

        Assert.NotNull(why);
        Assert.Contains("'pinned'", why, StringComparison.Ordinal);
        Assert.Contains("hashicorp/bionic64 1.0.0", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"version": 2, "machines": {}}""")]
    public async Task RefusesWhenTheMachineIndexCannotBeReadAtTheClean(string content)
    {
        var step = await PlannedPrune();

        File.WriteAllText(IndexPath, content);

        Assert.Contains(IndexPath, WhyNot(step), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesWhenWindowsWillNotSayWhetherTheMachineIndexIsThere()
    {
        var step = await PlannedPrune();

        using var denied = DeniedDirectory.WithUnreadableFile(IndexPath);
        Assert.Equal(PathPresence.Refused, LongPath.ProbeFile(IndexPath));

        Assert.Contains("Windows would not say", WhyNot(step), StringComparison.Ordinal);
    }

    /// <summary>A folder that will not be listed at the clean could hold a newer version.</summary>
    [Fact]
    public async Task RefusesWhenABoxFolderCannotBeListedAtTheClean()
    {
        var step = await PlannedPrune();
        var version = Path.Combine(BoxesIn(Home), "hashicorp-VAGRANTSLASH-bionic64", "1.0.282");

        using var denied = new DeniedDirectory(version);

        Assert.Contains("could not read every box folder", WhyNot(step), StringComparison.Ordinal);
    }

    /// <summary>
    /// The same refusal at planning: a layout read in part cannot be compared at the clean, so Vagrant
    /// is not even asked.
    /// </summary>
    [Fact]
    public async Task OffersNothingWhenABoxFolderCannotBeListedWhilePlanning()
    {
        InstallBionic(Home);
        var runner = Runner;

        using (new DeniedDirectory(Path.Combine(BoxesIn(Home), "hashicorp-VAGRANTSLASH-bionic64", "1.0.282")))
        {
            var plan = await new VagrantSupersededBoxProvider(_environment, runner, FakeProcessInspector.NothingRunning, system: _system)
                .PlanAsync();

            Assert.True(plan.IsEmpty);
            Assert.True(plan.WasNotExamined);
            Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(BoxesIn(Home), StringComparison.OrdinalIgnoreCase));
        }

        Assert.Empty(runner.Invocations);
    }
}
