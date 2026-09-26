using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which rows a run owes a check of what they leave standing: their kept items, and their Outlook data
/// files. Every row the run does not clean owes it, and the declined row is the one that matters most:
/// it was ticked, so it is not "unticked", and it does not run, so its own plan checks nothing.
/// </summary>
public sealed class StandingProofTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public StandingProofTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private PlaywrightBrowsersProvider Provider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>A Playwright cache holding two builds, each with a payload.</summary>
    private string CreateBuilds()
    {
        var root = Path.Combine(_environment.LocalAppData, "ms-playwright");

        foreach (var build in new[] { "chromium-1228", "firefox-1532" })
        {
            Directory.CreateDirectory(Path.Combine(root, build));
            File.WriteAllBytes(Path.Combine(root, build, "payload.bin"), new byte[4096]);
        }

        return root;
    }

    private static IReadOnlySet<string> KeepingChromium() =>
        new KeepList([new KeptItem("playwright", "Playwright browsers", new ItemIdentity("chromium-1228", "chromium-1228"))])
            .KeysFor("playwright");

    /// <summary>A Playwright cache holding two builds, with <c>chromium-1228</c> kept.</summary>
    private async Task<Finding> PlaywrightRowKeepingChromium()
    {
        CreateBuilds();

        var provider = Provider();

        return new Finding(provider, IsPresent: true, (await provider.PlanAsync()).WithKeepList(KeepingChromium()));
    }

    [Fact]
    public async Task ARowThatDoesNotRunOwesTheCheckOfItsKeptItems()
    {
        var row = await PlaywrightRowKeepingChromium();

        // Declined at §7, or never ticked: either way it is not among the findings that run.
        var owed = Assert.Single(StandingProof.For([row], running: []));

        Assert.Same(row.Provider, owed.Provider);
        Assert.NotNull(owed.Plan);
        Assert.Empty(owed.Plan.Steps);
        Assert.NotEmpty(owed.Plan.ProtectedPaths);
        Assert.All(owed.Plan.ProtectedPaths, p => Assert.Equal(Withholding.OnKeepList, p.Withheld));
    }

    /// <summary>
    /// §9 on the same terms. An Outlook data file a row found is protected on its plan, and a row that
    /// does not run checks nothing of its own, so an over-broad rule elsewhere in the run could take the
    /// store and nothing would say so.
    /// </summary>
    [Fact]
    public async Task ARowThatDoesNotRunOwesTheCheckOfItsOutlookDataFiles()
    {
        var root = CreateBuilds();
        var store = Path.Combine(root, "firefox-1532", "archive.pst");
        File.WriteAllBytes(store, new byte[8192]);

        var provider = Provider();
        var row = new Finding(provider, IsPresent: true, await provider.PlanAsync());

        var owed = Assert.Single(StandingProof.For([row], running: []));

        Assert.NotNull(owed.Plan);
        Assert.Empty(owed.Plan.Steps);
        Assert.True(owed.Plan.HasSomethingToProve);
        var protection = Assert.Single(owed.Plan.ProtectedPaths);
        Assert.Equal(store, protection.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Withholding.MailStore, protection.Withheld);
    }

    /// <summary>A row holding both owes both, and still nothing that guards its own deletion.</summary>
    [Fact]
    public async Task ARowHoldingBothOwesTheCheckOfBoth()
    {
        var root = CreateBuilds();
        File.WriteAllBytes(Path.Combine(root, "firefox-1532", "archive.pst"), new byte[8192]);

        var provider = Provider();
        var row = new Finding(provider, IsPresent: true, (await provider.PlanAsync()).WithKeepList(KeepingChromium()));

        var owed = Assert.Single(StandingProof.For([row], running: []));

        Assert.Contains(owed.Plan!.ProtectedPaths, p => p.Withheld == Withholding.OnKeepList);
        Assert.Contains(owed.Plan.ProtectedPaths, p => p.Withheld == Withholding.MailStore);
        Assert.All(owed.Plan.ProtectedPaths, p => Assert.NotEqual(Withholding.None, p.Withheld));
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
        Assert.Empty(StandingProof.For([row], running: [narrowed]));
    }

    [Fact]
    public async Task ARowKeepingNothingOwesNothing()
    {
        var provider = Provider();
        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "ms-playwright", "chromium-1228"));

        var row = new Finding(provider, IsPresent: true, await provider.PlanAsync());

        Assert.Empty(StandingProof.For([row], running: []));
    }
}
