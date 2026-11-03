using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The builds CapCut leaves in its <c>Apps</c> folder. The build the launcher starts is the one thing
/// that must never go, so most of these are about what is kept: the build the record names, the newest
/// build, the launcher, the record, and every build whenever the record does not say plainly which one
/// starts.
/// </summary>
public sealed class CapCutSupersededBuildProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public CapCutSupersededBuildProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string Apps => Path.Combine(_environment.LocalAppData, "CapCut", "Apps");

    private string Build(string version) => Path.Combine(Apps, version);

    private CapCutSupersededBuildProvider CreateProvider(ILiveTreeInspector? liveTrees = null) =>
        new(_environment, liveTrees ?? FakeLiveTreeInspector.NothingLive, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>An installation holding <paramref name="versions"/>, whose record names <paramref name="current"/>.</summary>
    private void Install(string? current, params string[] versions)
    {
        Directory.CreateDirectory(Apps);
        File.WriteAllBytes(Path.Combine(Apps, "CapCut.exe"), new byte[64]);
        File.WriteAllBytes(Path.Combine(Apps, "uninst.exe"), new byte[64]);

        foreach (var version in versions)
        {
            Directory.CreateDirectory(Build(version));
            File.WriteAllBytes(Path.Combine(Build(version), "CapCut.exe"), new byte[4096]);
        }

        if (current is not null)
        {
            File.WriteAllText(Path.Combine(Apps, "configure.ini"), $"[Configure]\r\nlast_version={current}\r\n");
        }
    }

    [Fact]
    public async Task ReportsNotPresentWhereCapCutIsNotInstalled()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task OffersEveryBuildButTheOneItStartsAtTier2WithoutPreSelectingIt()
    {
        Install("6.6.0.2610", "3.8.0.1431", "4.8.0.1820", "6.6.0.2610");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { Build("3.8.0.1431"), Build("4.8.0.1820") },
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(SafetyTier.RegenerableWithCost, plan.Tier);
        Assert.Equal(StepGrain.Items, provider.Grain);
        Assert.False(new Finding(provider, IsPresent: true, plan).IsPreSelectedByDefault);
    }

    /// <summary>
    /// Ordered by number, not by text: <c>10.0</c> is newer than <c>9.2</c>, and a text order would call
    /// the newest build superseded.
    /// </summary>
    [Fact]
    public async Task OrdersBuildsByNumberRatherThanByText()
    {
        Install("10.0.0.100", "9.2.0.3931", "10.0.0.100");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Build("9.2.0.3931")], plan.TargetedPaths);
    }

    /// <summary>
    /// A version-lock tool pins the launcher to an old build. That build is the one that starts, and the
    /// newest is kept too, because the next start after the lock is lifted may use it.
    /// </summary>
    [Fact]
    public async Task KeepsAPinnedOldBuildAndTheNewestBuild()
    {
        Install("4.8.0.1820", "3.8.0.1431", "4.8.0.1820", "6.6.0.2610");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([Build("3.8.0.1431")], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Build("4.8.0.1820"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Build("6.6.0.2610"), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>§5.6: the run removes the superseded builds and nothing else in <c>Apps</c>, asserted by name.</summary>
    [Fact]
    public async Task TheBuildItStartsTheLauncherAndEverythingElseSurviveARun()
    {
        Install("6.6.0.2610", "3.8.0.1431", "6.6.0.2610");
        var staging = Path.Combine(Apps, "20251103120000_1");
        Directory.CreateDirectory(staging);
        var service = Path.Combine(Apps, "Service");
        Directory.CreateDirectory(service);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var path in new[] { Apps, Build("6.6.0.2610"), Path.Combine(Apps, "CapCut.exe"), Path.Combine(Apps, "configure.ini") })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(Build("3.8.0.1431")));
        Assert.All([Build("6.6.0.2610"), staging, service], path => Assert.True(Directory.Exists(path), $"{path} went with an old build."));
        Assert.True(File.Exists(Path.Combine(Apps, "uninst.exe")));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task VerificationFailsLoudlyIfTheBuildItStartsVanished()
    {
        Install("6.6.0.2610", "3.8.0.1431", "6.6.0.2610");

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Directory.Delete(Build("6.6.0.2610"), recursive: true);

        Assert.False((await provider.VerifyAsync(plan)).Passed);
    }

    public static TheoryData<string?, string> Doubts => new()
    {
        { null, "does not name one build" },
        { "[Configure]\r\nlast_version=\r\n", "does not name one build" },
        { "[Configure]\r\nlast_version=3.8.0.1431\r\n[Version]\r\nlast_version=6.6.0.2610\r\n", "does not name one build" },
        { "[Configure]\r\nlast_version=7.0.0.1\r\n", "which is not there" },
    };

    /// <summary>
    /// Wherever the record does not name one build that is there, Deguffer cannot tell which one starts,
    /// so every build is left alone, the row says why, and it does not read as clear.
    /// </summary>
    [Theory]
    [MemberData(nameof(Doubts))]
    public async Task LeavesEveryBuildAloneWhereTheRecordDoesNotSayWhichStarts(string? record, string reason)
    {
        Install(current: null, "3.8.0.1431", "4.8.0.1820", "6.6.0.2610");

        if (record is not null)
        {
            File.WriteAllText(Path.Combine(Apps, "configure.ini"), record);
        }

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains(reason, StringComparison.Ordinal));
        Assert.False(new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory()).MayRemove(Build("3.8.0.1431")).IsAllowed);
    }

    [Fact]
    public async Task AnUnorderableBuildNumberLeavesEveryBuildAlone()
    {
        Install("6.6.0.2610", "3.8.0.1431", "6.6.0.2610", "99999999999.0.0.1");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("too large to order", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoFoldersNamingOneNumberLeaveEveryBuildAlone()
    {
        Install("6.6.0.2610", "3.8.0.1431", "6.6.0.2610", "06.6.0.2610");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.Contains(plan.Notes, n => n.Message.Contains("name the same number", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.2 and §5.6: a CapCut moved to another drive with a link is never removed through it. Every
    /// survivor would be checked through the same link, so a run there would prove nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(LinkedFolders))]
    public async Task ALinkedAppsOrEditorFolderIsNeverRemovedThrough(DirectoryLinkKind kind, bool linkTheEditorFolder)
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        var outsideApps = linkTheEditorFolder ? Path.Combine(outside, "Apps") : outside;

        foreach (var version in new[] { "3.8.0.1431", "6.6.0.2610" })
        {
            Directory.CreateDirectory(Path.Combine(outsideApps, version));
            File.WriteAllBytes(Path.Combine(outsideApps, version, "CapCut.exe"), new byte[64]);
        }

        File.WriteAllText(Path.Combine(outsideApps, "configure.ini"), "[Configure]\r\nlast_version=6.6.0.2610\r\n");

        var link = linkTheEditorFolder ? Path.GetDirectoryName(Apps)! : Apps;
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        DirectoryLink.Create(kind, link, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("through a link", StringComparison.Ordinal));
        Assert.False(new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory()).MayRemove(Build("3.8.0.1431")).IsAllowed);

        await provider.ExecuteAsync(plan);
        Assert.True(Directory.Exists(Path.Combine(outsideApps, "3.8.0.1431")), "an old build was removed through a link.");
    }

    public static TheoryData<DirectoryLinkKind, bool> LinkedFolders()
    {
        var data = new TheoryData<DirectoryLinkKind, bool>();

        foreach (var kind in DirectoryLink.Kinds.Select(row => (DirectoryLinkKind)row[0]))
        {
            data.Add(kind, false);
            data.Add(kind, true);
        }

        return data;
    }

    [Fact]
    public async Task OneBuildIsNothingToOfferAndReadsAsClear()
    {
        Install("6.6.0.2610", "6.6.0.2610");

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.False(plan.WasNotExamined);
    }

    /// <summary>§5.3: anything running from <c>Apps</c> holds every old build back, and Explore refuses them.</summary>
    [Fact]
    public async Task AnEditorThatIsRunningGivesUpNothing()
    {
        Install("6.6.0.2610", "3.8.0.1431", "6.6.0.2610");

        var provider = CreateProvider(new FakeLiveTreeInspector(Apps));
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("it is running", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(Build("3.8.0.1431"), StringComparison.OrdinalIgnoreCase));

        var held = Assert.Single(await provider.DiscoverToolRootsAsync());
        Assert.Equal(Build("3.8.0.1431"), held.Path);
        Assert.False(new ExploreActionPolicy([], [.. provider.ToolRoots, held], new FakeVolumeInventory()).MayRemove(Build("3.8.0.1431")).IsAllowed);

        await provider.ExecuteAsync(plan);
        Assert.True(Directory.Exists(Build("3.8.0.1431")), "an old build was removed while CapCut was running");
    }

    [Fact]
    public async Task AMachineWhereLivenessCannotBeEstablishedOffersNothingAndSaysSo()
    {
        Install("6.6.0.2610", "3.8.0.1431", "6.6.0.2610");

        var provider = CreateProvider(FakeLiveTreeInspector.CannotTell);
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("could not check", StringComparison.Ordinal));

        var held = Assert.Single(await provider.DiscoverToolRootsAsync());
        Assert.Contains("could not tell whether CapCut is running", held.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DirectoryLink.Kinds), MemberType = typeof(DirectoryLink))]
    public async Task ALinkedOldBuildIsNamedRatherThanFollowed(DirectoryLinkKind kind)
    {
        Install("6.6.0.2610", "6.6.0.2610");
        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "irreplaceable.bin"), new byte[64]);
        DirectoryLink.Create(kind, Build("3.8.0.1431"), outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);
        Assert.True(File.Exists(Path.Combine(outside, "irreplaceable.bin")), "a linked build was deleted through.");
    }

    [Theory]
    [InlineData("3.8.0.1431", true)]
    [InlineData("4.8.0.1820", false)]
    [InlineData("6.6.0.2610", false)]
    [InlineData("20251103120000_1", false)]
    [InlineData("Service", false)]
    [InlineData("", false)]
    public void ExploreAllowsOnlyTheSupersededBuilds(string child, bool allowed)
    {
        Install("4.8.0.1820", "3.8.0.1431", "4.8.0.1820", "6.6.0.2610");
        Directory.CreateDirectory(Path.Combine(Apps, "20251103120000_1"));
        Directory.CreateDirectory(Path.Combine(Apps, "Service"));

        var policy = new ExploreActionPolicy([], CreateProvider().ToolRoots, new FakeVolumeInventory());

        Assert.Equal(allowed, policy.MayRemove(child.Length == 0 ? Apps : Path.Combine(Apps, child)).IsAllowed);
    }

    [Fact]
    public async Task CleanedPlacesCoverEveryPathThePlanCleans()
    {
        Install("6.6.0.2610", "3.8.0.1431", "4.8.0.1820", "6.6.0.2610");

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(CreateProvider()));
    }

    [Theory]
    [InlineData("[Configure]\nlast_version=3.8.0.1431\n", "3.8.0.1431")]
    [InlineData("﻿[Version]\r\n LAST_VERSION = 3.8.0.1431 \r\n", "3.8.0.1431")]
    [InlineData("[jianyingpro]\r\nlast_version=5.9.0.11632\r\nlast_version=5.9.0.11632\r\n", "5.9.0.11632")]
    public void ReadsTheKeyInAnySection(string text, string expected)
    {
        var record = CapCutLaunchRecord.Parse(text);

        Assert.Equal(CapCutRecordState.Named, record.State);
        Assert.Equal(expected, record.Version);
    }
}
