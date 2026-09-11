using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which rows a run owes a check of their kept items. Every row the run does not clean owes it, and
/// the declined row is the one that matters most: it was ticked, so it is not "unticked", and it does
/// not run, so its own plan checks nothing.
/// </summary>
public sealed class KeepListProofTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public KeepListProofTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary>A Playwright cache holding two builds, with <c>chromium-1228</c> kept.</summary>
    private async Task<Finding> PlaywrightRowKeepingChromium()
    {
        var root = Path.Combine(_environment.LocalAppData, "ms-playwright");

        foreach (var build in new[] { "chromium-1228", "firefox-1532" })
        {
            Directory.CreateDirectory(Path.Combine(root, build));
            File.WriteAllBytes(Path.Combine(root, build, "payload.bin"), new byte[4096]);
        }

        var provider = new PlaywrightBrowsersProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);
        var keys = new KeepList(
            [new KeptItem("playwright", "Playwright browsers", new ItemIdentity("chromium-1228", "chromium-1228"))])
            .KeysFor("playwright");

        return new Finding(provider, IsPresent: true, (await provider.PlanAsync()).WithKeepList(keys));
    }

    [Fact]
    public async Task ARowThatDoesNotRunOwesTheCheckOfItsKeptItems()
    {
        var row = await PlaywrightRowKeepingChromium();

        // Declined at §7, or never ticked: either way it is not among the findings that run.
        var owed = Assert.Single(KeepListProof.For([row], running: []));

        Assert.Same(row.Provider, owed.Provider);
        Assert.NotNull(owed.Plan);
        Assert.Empty(owed.Plan.Steps);
        Assert.NotEmpty(owed.Plan.ProtectedPaths);
        Assert.All(owed.Plan.ProtectedPaths, p => Assert.Equal(Withholding.OnKeepList, p.Withheld));
    }

    /// <summary>
    /// A row that runs checks its kept items itself, because narrowing keeps them protected, so it owes
    /// no second check. It is matched by provider: what runs is the row's plan narrowed to its ticks,
    /// a different plan from the one it previewed.
    /// </summary>
    [Fact]
    public async Task ARowThatRunsOwesNothingMore()
    {
        var row = await PlaywrightRowKeepingChromium();
        var narrowed = row with { Plan = row.Plan!.NarrowedTo(row.Plan.Steps) };

        Assert.Contains(narrowed.Plan!.ProtectedPaths, p => p.Withheld == Withholding.OnKeepList);
        Assert.Empty(KeepListProof.For([row], running: [narrowed]));
    }

    [Fact]
    public async Task ARowKeepingNothingOwesNothing()
    {
        var provider = new PlaywrightBrowsersProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);
        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "ms-playwright", "chromium-1228"));

        var row = new Finding(provider, IsPresent: true, await provider.PlanAsync());

        Assert.Empty(KeepListProof.For([row], running: []));
    }
}
