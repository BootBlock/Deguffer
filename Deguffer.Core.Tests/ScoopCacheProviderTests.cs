using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.1: Scoop's own <c>scoop cache rm *</c>, never a deletion. §5.2: that command removes whatever
/// the folder holds, so it is offered only where every entry is a download Scoop made.
/// </summary>
public sealed class ScoopCacheProviderTests : IDisposable
{
    private readonly ScoopFixture _scoop = new();

    public void Dispose() => _scoop.Dispose();

    [Fact]
    public async Task ReportsNotPresentWhenScoopIsNotInstalled()
    {
        var provider = _scoop.Provider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task PlansScoopsOwnCommandByTheFullPathOfItsCmdShim()
    {
        var shim = _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download, 8192);

        var plan = await _scoop.Provider().PlanAsync();
        var step = ScoopFixture.Step(plan);

        Assert.Empty(plan.TargetedPaths);
        Assert.Equal(shim, step.FileName);
        Assert.Equal("cache rm *", step.Arguments);
        Assert.Equal(CancelledCommand.Ended, step.WhenCancelled);
        Assert.Equal(8192, step.EstimatedBytes);
        Assert.Empty(_scoop.Runner.Invocations);
    }

    [Fact]
    public async Task AnAbsentCacheOffersNothingAndStartsNothing()
    {
        _scoop.Install();

        var plan = await _scoop.Provider().PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.False(plan.WasNotExamined);
        Assert.Empty(_scoop.Runner.Invocations);
    }

    [Fact]
    public async Task AnEmptyCacheOffersNothing()
    {
        _scoop.Install();
        Directory.CreateDirectory(_scoop.DefaultCache);

        Assert.True((await _scoop.Provider().PlanAsync()).IsEmpty);
    }

    /// <summary>A refusal is never read as an absent cache, and never as one that is clear.</summary>
    [Fact]
    public async Task ARefusedCacheIsReportedAsUnreachedRatherThanAbsent()
    {
        _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);

        using (DeniedDirectory.WithUnreadableAttributes(_scoop.DefaultCache))
        {
            var plan = await _scoop.Provider().PlanAsync();

            Assert.Empty(plan.Steps);
            Assert.True(plan.HasUnreadableRoot);
        }

        Assert.Empty(_scoop.Runner.Invocations);
    }

    [Fact]
    public async Task ACacheThatWillNotBeListedOffersNothing()
    {
        _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);

        using (new DeniedDirectory(_scoop.DefaultCache))
        {
            var plan = await _scoop.Provider().PlanAsync();

            Assert.Empty(plan.Steps);
            Assert.True(plan.HasUnreadableRoot);
        }
    }

    [Fact]
    public async Task ALinkedCacheIsLeftAlone()
    {
        _scoop.Install();
        var target = _scoop.Elsewhere("real-cache");
        _scoop.File(target, ScoopFixture.Download);
        Junction.ToDirectory(_scoop.DefaultCache, target);

        var plan = await _scoop.Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
    }

    /// <summary>
    /// Every name Scoop gives what it writes into the cache: both <c>cache_path</c> forms, the
    /// built-in downloader's partial, aria2's control file, and aria2's input list beside a download
    /// of the same app.
    /// </summary>
    [Theory]
    [InlineData("git#2.47.1#https_github.com_git-for-windows_git_releases_download_PortableGit-64-bit.7z.exe")]
    [InlineData("7zip#24.08#a1b2c3d.msi")]
    [InlineData("nodejs#22.11.0#0f9e8d7.zip_dl=1&x=2")]
    [InlineData("7zip#24.08#a1b2c3d.msi.download")]
    [InlineData("7zip#24.08#a1b2c3d.msi.aria2")]
    [InlineData("git.txt")]
    public async Task OffersTheCommandWhereEveryEntryIsOneScoopWrites(string name)
    {
        _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);
        _scoop.File(_scoop.DefaultCache, name);

        var step = ScoopFixture.Step(await _scoop.Provider().PlanAsync());

        Assert.Equal(8192, step.EstimatedBytes);
    }

    /// <summary>
    /// The command removes everything it lists, so one entry Scoop did not make withholds it, and
    /// that entry's survival is what the plan asserts. An empty folder counts: <c>Remove-Item</c>
    /// takes one without asking.
    /// </summary>
    [Theory]
    [InlineData("notes.txt", false)]
    [InlineData("7zip.txt", false)]
    [InlineData("git#2.47.1", false)]
    [InlineData("git#2.47.1#a b.zip", false)]
    [InlineData("installers", true)]
    public async Task AnEntryScoopDidNotMakeWithholdsTheCommandAndIsProtected(string name, bool folder)
    {
        _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);
        var stranger = Path.Combine(_scoop.DefaultCache, name);

        if (folder)
        {
            Directory.CreateDirectory(stranger);
        }
        else
        {
            _scoop.File(_scoop.DefaultCache, name);
        }

        var plan = await _scoop.Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, note => note.Message.Contains($"'{name}'", StringComparison.Ordinal));
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(stranger, StringComparison.OrdinalIgnoreCase)
            && p.PresenceBefore is PathPresence.Present);
        Assert.DoesNotContain(plan.ProtectedPaths, p => p.Path.EndsWith(ScoopFixture.Download, StringComparison.Ordinal));
        Assert.Empty(_scoop.Runner.Invocations);
    }

    [Fact]
    public async Task ALinkInsideTheCacheWithholdsTheCommand()
    {
        _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);
        var target = _scoop.Elsewhere("linked");
        Directory.CreateDirectory(target);
        Junction.ToDirectory(Path.Combine(_scoop.DefaultCache, "git#2.47.1#0123abc.7z"), target);

        Assert.Empty((await _scoop.Provider().PlanAsync()).Steps);
    }

    /// <summary>
    /// §5.6, with the command run as Scoop runs it: every top-level file goes and the folder stays.
    /// The verification proves the cache folder, Scoop's own folders and its settings outlived it.
    /// </summary>
    [Fact]
    public async Task VerifiesTheCacheFolderAndEverythingBesideItSurvived()
    {
        var shim = _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download, 8192);
        var app = _scoop.File(Path.Combine(_scoop.DefaultRoot, "apps", "git", "2.47.1"), "git.exe");
        _scoop.WriteText(_scoop.UserConfig, """{ "gh_token": "<YOUR_API_KEY>" }""");
        _scoop.Runner.Replying(shim, arguments => arguments == "cache rm *" ? RemoveEveryFileIn(_scoop.DefaultCache) : null);
        var provider = _scoop.Provider();

        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);
        var verification = await provider.VerifyAsync(plan);

        Assert.Equal(8192, result.BytesReclaimed);
        Assert.True(verification.Passed);
        Assert.True(Directory.Exists(_scoop.DefaultCache));
        Assert.True(File.Exists(app));
        Assert.True(File.Exists(_scoop.UserConfig));
    }

    /// <summary>The negative bites: a command that took the installed apps as well fails verification.</summary>
    [Fact]
    public async Task VerificationFailsWhereTheCommandTookTheInstalledApps()
    {
        var shim = _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);
        _scoop.File(Path.Combine(_scoop.DefaultRoot, "apps", "git", "2.47.1"), "git.exe");
        _scoop.Runner.Replying(shim, arguments =>
        {
            Directory.Delete(Path.Combine(_scoop.DefaultRoot, "apps"), recursive: true);
            return RemoveEveryFileIn(_scoop.DefaultCache);
        });
        var provider = _scoop.Provider();

        var plan = await provider.PlanAsync();
        await provider.ExecuteAsync(plan);

        Assert.False((await provider.VerifyAsync(plan)).Passed);
    }

    [Fact]
    public async Task ProtectsScoopsFoldersAndBothSettingsFiles()
    {
        _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);
        Directory.CreateDirectory(Path.Combine(_scoop.DefaultRoot, "shims"));
        var portable = Path.Combine(_scoop.DefaultRoot, "config.json");

        var plan = await _scoop.Provider().PlanAsync();
        var protectedPaths = plan.ProtectedPaths.Select(p => p.Path).ToList();

        Assert.Contains(_scoop.DefaultCache, protectedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(portable, protectedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(_scoop.UserConfig, protectedPaths, StringComparer.OrdinalIgnoreCase);

        foreach (var folder in new[] { "apps", "persist", "buckets", "shims" })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(Path.Combine(_scoop.DefaultRoot, folder), StringComparison.OrdinalIgnoreCase)
                && p.PresenceBefore is PathPresence.Present);
        }
    }

    /// <summary>
    /// §7.1 at the default folders: the root, its apps and the settings are refused, and so is an
    /// entry in the cache that is not a download, while a download may go.
    /// </summary>
    [Fact]
    public void ExploreRefusesWhatThePlanProtects()
    {
        var download = _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);
        var notes = _scoop.File(_scoop.DefaultCache, "notes.txt");
        _scoop.WriteText(_scoop.UserConfig, "{}");
        var policy = new ExploreActionPolicy([], _scoop.Provider().ToolRoots, new FakeVolumeInventory());

        Assert.False(policy.MayRemove(_scoop.DefaultRoot).IsAllowed);
        Assert.False(policy.MayRemove(_scoop.DefaultCache).IsAllowed);
        Assert.False(policy.MayRemove(Path.Combine(_scoop.DefaultRoot, "apps")).IsAllowed);
        Assert.False(policy.MayRemove(_scoop.UserConfig).IsAllowed);
        Assert.False(policy.MayRemove(notes).IsAllowed);
        Assert.True(policy.MayRemove(download).IsAllowed);
    }

    [Fact]
    public async Task DiscoveredRootsFollowTheCacheScoopUses()
    {
        _scoop.Install();
        var cache = _scoop.Elsewhere("variable-cache");
        _scoop.Environment.WithEnvironmentVariable("SCOOP_CACHE", cache);

        var roots = await _scoop.Provider().DiscoverToolRootsAsync();

        Assert.Contains(roots, root => root.Path.Equals(cache, StringComparison.OrdinalIgnoreCase)
            && root.Recognises(new ToolRootChild(ScoopFixture.Download, ChildKind.File))
            && !root.Recognises(new ToolRootChild("notes.txt", ChildKind.File))
            && !root.Recognises(new ToolRootChild(ScoopFixture.Download, ChildKind.Folder)));
    }

    [Fact]
    public async Task CleanedPlacesCoverTheCacheTheCommandIsSentTo()
    {
        _scoop.Install();
        var cache = _scoop.Elsewhere("variable-cache");
        _scoop.Environment.WithEnvironmentVariable("SCOOP_CACHE", cache);
        _scoop.File(cache, ScoopFixture.Download);

        Assert.Empty(await CleanedPlaceCoverage.UncoveredAsync(_scoop.Provider()));
    }

    private static CommandOutcome RemoveEveryFileIn(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            File.Delete(file);
        }

        return new CommandOutcome(0, "Deleted: 1 file, 8 KB", string.Empty);
    }
}
