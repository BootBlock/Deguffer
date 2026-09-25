using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which rows the re-plan after a clean measures again. Too few leaves a row stating a figure the run
/// changed, beside a Clean button that acts on it. Too many is the full rescan the re-plan replaces.
/// </summary>
public sealed class RunChangesTests
{
    private const string Profile = @"C:\Users\testuser";

    private const string Temp = Profile + @"\AppData\Local\Temp";

    [Fact]
    public void ARowThatRanIsPlannedAgainAndAnUntouchedRowIsNot()
    {
        var npm = Row("npm", Deletes(Profile + @"\AppData\Local\npm-cache\_cacache"));
        var gradle = Row("gradle", Deletes(Profile + @"\.gradle\caches"));

        Assert.Equal(["npm"], Ids(RunChanges.Stale([npm, gradle], [npm])));
    }

    /// <summary>
    /// NuGet's own command empties <c>%TEMP%\NuGetScratch</c>, which the temporary files row lists as
    /// one of its items. The temporary files row did not run, and its figure still changed.
    /// </summary>
    [Fact]
    public void ARowListingAFolderACommandWasDeclaredToReachIsPlannedAgain()
    {
        var nuget = Row("nuget", Runs(Profile + @"\.nuget\packages", Temp + @"\NuGetScratch"));
        var temp = Row("temp", Deletes(Temp + @"\NuGetScratch"), Deletes(Temp + @"\other"));

        Assert.Equal(["nuget", "temp"], Ids(RunChanges.Stale([nuget, temp], [nuget])));
    }

    [Fact]
    public void ARowHoldingAFolderTheRunDeletedIsPlannedAgain()
    {
        var temp = Row("temp", Clears(Temp));
        var app = Row("app", Deletes(Temp + @"\SomeApp\cache"));

        Assert.Equal(["temp", "app"], Ids(RunChanges.Stale([temp, app], [app])));
    }

    [Fact]
    public void ARowInsideAFolderTheRunDeletedIsPlannedAgain()
    {
        var temp = Row("temp", Clears(Temp));
        var app = Row("app", Deletes(Temp + @"\SomeApp\cache"));

        Assert.Equal(["temp", "app"], Ids(RunChanges.Stale([temp, app], [temp])));
    }

    /// <summary>A shared prefix is not a shared folder: <c>cache2</c> is not inside <c>cache</c>.</summary>
    [Fact]
    public void ASiblingWhoseNameStartsTheSameIsNotPlannedAgain()
    {
        var first = Row("first", Deletes(Profile + @"\cache"));
        var second = Row("second", Deletes(Profile + @"\cache2"));

        Assert.Equal(["first"], Ids(RunChanges.Stale([first, second], [first])));
    }

    /// <summary>
    /// §6.3: a step's path may carry the extended-length prefix and a command's declared path does
    /// not. Compared as they arrive, the two name the same folder and share no prefix.
    /// </summary>
    [Fact]
    public void AnExtendedLengthPathMatchesTheSameFolderWithoutThePrefix()
    {
        var nuget = Row("nuget", Runs(Temp + @"\NuGetScratch"));
        var temp = Row("temp", Deletes(@"\\?\" + Temp + @"\NuGetScratch"));

        Assert.Equal(["nuget", "temp"], Ids(RunChanges.Stale([nuget, temp], [nuget])));
    }

    /// <summary>
    /// Removing a project's <c>node_modules</c> releases the pnpm store files that were linked into it,
    /// and no path of the store's is shared with the project's.
    /// </summary>
    [Fact]
    public void ARowCountingLinksFromElsewhereIsPlannedAgainAfterAnyRemoval()
    {
        var modules = Row("node_modules", Deletes(@"C:\Source\app\node_modules"));
        var pnpm = CountingLinks(Row("pnpm", Runs(Profile + @"\AppData\Local\pnpm\store\v10")));

        Assert.Equal(["node_modules", "pnpm"], Ids(RunChanges.Stale([modules, pnpm], [modules])));
    }

    /// <summary>
    /// A row run only to prove what it left standing removes nothing, so nothing it reports on can
    /// have changed, and a row counting links has nothing new to count.
    /// </summary>
    [Fact]
    public void ARunThatRemovedNothingPlansNothingAgain()
    {
        var proof = Row("proof");
        var pnpm = CountingLinks(Row("pnpm", Runs(Profile + @"\AppData\Local\pnpm\store\v10")));

        Assert.Empty(RunChanges.Stale([proof, pnpm], [proof]));
    }

    [Fact]
    public void AnAbsentToolIsNeverPlannedAgainForARunThatDidNotInvolveIt()
    {
        var npm = Row("npm", Deletes(Profile + @"\AppData\Local\npm-cache\_cacache"));
        var absent = new Finding(new NamedProvider("absent"), IsPresent: false, Plan: null);

        Assert.Equal(["npm"], Ids(RunChanges.Stale([absent, npm], [npm])));
    }

    [Fact]
    public void TheRowsComeBackInTheOrderThePageShowsThem()
    {
        var temp = Row("temp", Clears(Temp));
        var app = Row("app", Deletes(Temp + @"\SomeApp\cache"));

        Assert.Equal(["app", "temp"], Ids(RunChanges.Stale([app, temp], [temp])));
    }

    private static string[] Ids(IReadOnlyList<ICleanupProvider> providers) => [.. providers.Select(p => p.Id)];

    private static Finding Row(string id, params CleanupStep[] steps) => new(
        new NamedProvider(id),
        IsPresent: true,
        new CleanupPlan
        {
            ProviderId = id,
            ProviderName = id,
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps = steps,
        });

    private static Finding CountingLinks(Finding row) =>
        row with { Plan = row.Plan! with { CountsLinksFromElsewhere = true } };

    private static DeleteDirectoryStep Deletes(string path) =>
        new(path, "Cache") { Estimated = new ScanSize(4096, 4096) };

    private static ClearDirectoryStep Clears(string path) =>
        new(path, "Contents") { Estimated = new ScanSize(4096, 4096) };

    private static RunCommandStep Runs(params string[] reaches) =>
        new("tool", "clear", "Clear") { Estimated = new ScanSize(4096, 4096), MeasuredPaths = reaches };

    /// <summary>Only its identity is read here; nothing is ever planned, run or verified.</summary>
    private sealed class NamedProvider(string id) : ICleanupProvider
    {
        public string Id => id;

        public string Name => id;

        public SafetyTier Tier => SafetyTier.RegenerableCache;

        public StepGrain Grain => StepGrain.Parts;

        public string WhatHappensOnNextUse => "Nothing.";

        public ProviderDescription Description { get; } = new()
        {
            Application = "A stub, standing in for a real toolchain.",
            Publisher = "Nobody.",
            Purpose = "Nothing. This provider exists only for this test.",
            Recommendation = "Nothing to recommend.",
        };

        public bool IsAwaitingSourceFolders => false;

        public IReadOnlyList<ToolRoot> ToolRoots => [];

        public Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public void InvalidateCaches() => throw new NotSupportedException();

        public Task<bool> IsPresentAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CleanupPlan> PlanAsync(MinimumAge keep = default, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<CleanupResult> ExecuteAsync(
            CleanupPlan plan,
            RunReach? runReach = null,
            RunResidue? residue = null,
            IProgress<double>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<VerificationResult> VerifyAsync(
            CleanupPlan plan, RunReach? runReach = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
