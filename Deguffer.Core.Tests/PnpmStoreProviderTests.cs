using System.Runtime.InteropServices;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// pnpm is a §5.1 provider whose estimate is the first link-aware one, so two negatives carry the
/// class: no path is ever a target, and a file some project still hard-links is never counted.
/// The link fixtures are real — a hard link costs nothing to create unelevated — so the central
/// claim is observed on every run rather than stubbed.
/// </summary>
public sealed class PnpmStoreProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public PnpmStoreProviderTests() =>
        _environment = new FakeUserEnvironment(_temp.Path).WithExecutable("pnpm");

    public void Dispose() => _temp.Dispose();

    private string Home => Path.Combine(_environment.LocalAppData, "pnpm");

    private string Store => Path.Combine(Home, "store", "v10");

    private FakeProcessRunner Reporting(string store) =>
        new FakeProcessRunner().Responding("store path", store + "\r\n");

    private PnpmStoreProvider CreateProvider(FakeProcessRunner? runner = null) =>
        new(_environment, runner ?? Reporting(Store), FakeProcessInspector.NothingRunning);

    private string Populate(string directory, int bytes = 4096)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[bytes]);
        return directory;
    }

    /// <summary>One store file, hard-linked into a "project" outside the store.</summary>
    private void LinkIntoProject(string storeFile)
    {
        var link = Path.Combine(_temp.Path, "project", "node_modules", Path.GetFileName(storeFile));
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        Assert.True(
            CreateHardLink(link, storeFile, securityAttributes: 0),
            $"CreateHardLink failed with error {Marshal.GetLastWin32Error()}.");
    }

    [Fact]
    public async Task ReportsNotPresentWhenPnpmIsNotInstalled()
    {
        var provider = new PnpmStoreProvider(
            new FakeUserEnvironment(_temp.Path), new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// §5.1 and the deliberate absence together: the one step is <c>store prune</c> with no
    /// <c>--force</c>, because pnpm's force means "also remove alien files" — directories the
    /// package manager did not create, which is precisely what §5.2 refuses to name as targets.
    /// </summary>
    [Fact]
    public async Task PlansPruneAloneAndNeverForce()
    {
        Populate(Store);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps.OfType<RunCommandStep>());
        Assert.Equal("store prune", step.Arguments);
        Assert.Equal([Store], step.MeasuredPaths);
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// The number this provider waited a phase for. Summing file lengths would count the linked
    /// file and promise 68 KB; pruning would free 4 KB; the §5.4 rule is that the smaller, true
    /// figure is the one shown.
    /// </summary>
    [Fact]
    public async Task TheEstimateExcludesFilesProjectsStillLink()
    {
        Populate(Store);
        var shared = Path.Combine(Store, "shared.bin");
        File.WriteAllBytes(shared, new byte[65536]);
        LinkIntoProject(shared);

        var plan = await CreateProvider().PlanAsync();

        // Logical rather than the reclaimable (allocated) figure, which rounds to the volume's
        // cluster size and would make this a claim about the disk the test runs on.
        Assert.Equal(4096, plan.Estimated.Logical);
        Assert.True(plan.Estimated.IsApproximate);
    }

    [Fact]
    public async Task OffersNothingWhenPnpmDoesNotSayWhereItsStoreIs()
    {
        foreach (var answer in new[] { "", "not-a-path", "relative\\store" })
        {
            var plan = await CreateProvider(new FakeProcessRunner().Responding("store path", answer)).PlanAsync();

            Assert.True(plan.IsEmpty);
            Assert.Contains(plan.Notes, n => n.Message.Contains("did not say where", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A volume root has no containing directory to protect and is a store no pnpm writes, so an
    /// answer naming one is treated as no answer rather than measured.
    /// </summary>
    [Fact]
    public async Task OffersNothingForAStoreAtAVolumeRoot()
    {
        var plan = await CreateProvider(new FakeProcessRunner().Responding("store path", @"C:\")).PlanAsync();

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public async Task SaysSoWhenTheStoreDoesNotExistYet()
    {
        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.Notes, n => n.Message.Contains("does not exist yet", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6, and the store heads the list. `pnpm store prune` works inside the store, so a command
    /// that took the whole directory would destroy every package on the machine — and a plan that
    /// named only the store's neighbours would report that run as a success.
    /// </summary>
    [Fact]
    public async Task AssertsTheStoreItselfAndItsNeighboursSurvive()
    {
        Populate(Store);
        Populate(Path.Combine(Home, "global"));

        var plan = await CreateProvider().PlanAsync();

        foreach (var path in (string[])[Store, Path.Combine(Home, "store"), Home, Path.Combine(Home, "global")])
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }
    }

    /// <summary>
    /// The §5.6 negative for the store itself. Every other check on this plan passes while the
    /// store is gone, which is exactly why the store has to be one of them.
    /// </summary>
    [Fact]
    public async Task VerificationFailsLoudlyIfThePruneTookTheWholeStore()
    {
        Populate(Store);
        Populate(Path.Combine(Home, "global"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(Store, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(Store, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HonoursPnpmHomeWhenItHasBeenMoved()
    {
        var moved = Path.Combine(_temp.Path, "moved-pnpm-home");
        _environment.WithEnvironmentVariable("PNPM_HOME", moved);
        Populate(Store);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(moved, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheGlobalPackagesVanished()
    {
        Populate(Store);
        var global = Populate(Path.Combine(Home, "global"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        // Simulate the over-broad command §5.6 exists to catch.
        Directory.Delete(global, recursive: true);

        var verification = await provider.VerifyAsync(plan);

        Assert.False(verification.Passed);
        Assert.Contains(verification.Failures, c => c.Path.Equals(global, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AsksPnpmForTheStoreOnceAndThenRemembers()
    {
        Populate(Store);
        var runner = Reporting(Store);
        var provider = CreateProvider(runner);

        await provider.PlanAsync();
        await provider.PlanAsync();

        Assert.Single(runner.Invocations);
        Assert.Equal("store path", runner.Invocations[0].Arguments);
    }

    /// <summary>
    /// <c>store-dir</c> configuration moves the store between scans, so a rescan has to ask again
    /// rather than measure a store pnpm has stopped using.
    /// </summary>
    [Fact]
    public async Task AsksAgainAfterAnInvalidation()
    {
        Populate(Store);
        var runner = Reporting(Store);
        var provider = CreateProvider(runner);

        await provider.PlanAsync();
        provider.InvalidateCaches();
        await provider.PlanAsync();

        Assert.Equal(2, runner.Invocations.Count);
    }

    [Fact]
    public async Task WarnsWhenNodeIsRunning()
    {
        Populate(Store);

        var provider = new PnpmStoreProvider(_environment, Reporting(Store), new FakeProcessInspector("node"));

        var plan = await provider.PlanAsync();

        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
}
