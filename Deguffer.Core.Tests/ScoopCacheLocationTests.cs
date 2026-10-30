using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Scoop's cache is found where Scoop's own start-up finds it, and nowhere else. The plan examines
/// that folder and the command empties the one Scoop names, so the two have to be the same folder,
/// and every value Deguffer cannot read as Scoop does offers nothing at all.
/// </summary>
public sealed class ScoopCacheLocationTests : IDisposable
{
    private readonly ScoopFixture _scoop = new();

    public void Dispose() => _scoop.Dispose();

    [Fact]
    public async Task FindsTheCacheInTheDefaultRoot()
    {
        _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);

        Assert.Equal([_scoop.DefaultCache], ScoopFixture.Step(await _scoop.Provider().PlanAsync()).MeasuredPaths);
    }

    /// <summary>
    /// With neither <c>SCOOP</c> nor <c>root_path</c> set, the root is four folders above the
    /// running Scoop's <c>lib</c>, wherever it was installed, not the profile's <c>scoop</c>.
    /// </summary>
    [Fact]
    public async Task FindsTheRootFromTheScoopTheShimRuns()
    {
        var root = _scoop.Elsewhere("tools", "scoop");
        _scoop.Install(root);
        var cache = Path.Combine(root, "cache");
        _scoop.File(cache, ScoopFixture.Download);
        _scoop.File(_scoop.DefaultCache, "someone else's notes.txt");

        Assert.Equal([cache], ScoopFixture.Step(await _scoop.Provider().PlanAsync()).MeasuredPaths);
    }

    [Fact]
    public async Task TheScoopVariableMovesTheRootAndItsCache()
    {
        _scoop.Install();
        var root = _scoop.Elsewhere("moved");
        _scoop.Environment.WithEnvironmentVariable("SCOOP", root);
        _scoop.WriteText(_scoop.UserConfig, $$"""{ "root_path": {{Json(_scoop.Elsewhere("ignored"))}} }""");
        _scoop.File(Path.Combine(root, "cache"), ScoopFixture.Download);

        Assert.Equal([Path.Combine(root, "cache")], ScoopFixture.Step(await _scoop.Provider().PlanAsync()).MeasuredPaths);
    }

    [Fact]
    public async Task TheRootPathSettingMovesTheRootAndItsCache()
    {
        _scoop.Install();
        var root = _scoop.Elsewhere("configured");
        _scoop.WriteText(_scoop.UserConfig, $$"""{ "root_path": {{Json(root)}} }""");
        _scoop.File(Path.Combine(root, "cache"), ScoopFixture.Download);

        Assert.Equal([Path.Combine(root, "cache")], ScoopFixture.Step(await _scoop.Provider().PlanAsync()).MeasuredPaths);
    }

    [Fact]
    public async Task TheScoopCacheVariableWinsOverTheSetting()
    {
        _scoop.Install();
        var cache = _scoop.Elsewhere("variable-cache");
        _scoop.Environment.WithEnvironmentVariable("SCOOP_CACHE", cache);
        _scoop.WriteText(_scoop.UserConfig, $$"""{ "cache_path": {{Json(_scoop.Elsewhere("setting-cache"))}} }""");
        _scoop.File(cache, ScoopFixture.Download);

        Assert.Equal([cache], ScoopFixture.Step(await _scoop.Provider().PlanAsync()).MeasuredPaths);
    }

    /// <summary>PowerShell's member access ignores case, so <c>get_config</c>'s lower-cased name finds any spelling.</summary>
    [Theory]
    [InlineData("cache_path")]
    [InlineData("CACHE_PATH")]
    [InlineData("Cache_Path")]
    public async Task TheCachePathSettingIsReadWhateverItsCase(string key)
    {
        _scoop.Install();
        var cache = _scoop.Elsewhere("setting-cache");
        _scoop.WriteText(_scoop.UserConfig, $$"""{ "{{key}}": {{Json(cache)}}, "gh_token": "<YOUR_API_KEY>" }""");
        _scoop.File(cache, ScoopFixture.Download);

        Assert.Equal([cache], ScoopFixture.Step(await _scoop.Provider().PlanAsync()).MeasuredPaths);
    }

    [Fact]
    public async Task APortableSettingsFileBesideTheInstallWinsOverTheUsersOwn()
    {
        _scoop.Install();
        var portable = _scoop.Elsewhere("portable-cache");
        _scoop.WriteText(Path.Combine(_scoop.DefaultRoot, "config.json"), $$"""{ "cache_path": {{Json(portable)}} }""");
        _scoop.WriteText(_scoop.UserConfig, $$"""{ "cache_path": {{Json(_scoop.Elsewhere("user-cache"))}} }""");
        _scoop.File(portable, ScoopFixture.Download);

        Assert.Equal([portable], ScoopFixture.Step(await _scoop.Provider().PlanAsync()).MeasuredPaths);
    }

    [Fact]
    public async Task XdgConfigHomeMovesTheSettingsFile()
    {
        _scoop.Install();
        var configHome = _scoop.Elsewhere("xdg");
        var cache = _scoop.Elsewhere("xdg-cache");
        _scoop.Environment.WithEnvironmentVariable("XDG_CONFIG_HOME", configHome);
        _scoop.WriteText(Path.Combine(configHome, "scoop", "config.json"), $$"""{ "cache_path": {{Json(cache)}} }""");
        _scoop.WriteText(_scoop.UserConfig, $$"""{ "cache_path": {{Json(_scoop.Elsewhere("user-cache"))}} }""");
        _scoop.File(cache, ScoopFixture.Download);

        Assert.Equal([cache], ScoopFixture.Step(await _scoop.Provider().PlanAsync()).MeasuredPaths);
    }

    /// <summary>
    /// Scoop resolves a relative value against PowerShell's current folder, and pads, device forms,
    /// wildcards and the backtick that escapes them are each read differently by <c>-Path</c> than by
    /// Win32. Each would have the
    /// plan examine one folder and the command empty another, so none of them offers anything, and
    /// Scoop is never started. The default cache is populated, so a value read as unset would plan.
    /// </summary>
    [Theory]
    [InlineData("SCOOP_CACHE", "cache")]
    [InlineData("SCOOP_CACHE", @"\scoop\cache")]
    [InlineData("SCOOP_CACHE", @"C:scoop\cache")]
    [InlineData("SCOOP_CACHE", " {0}")]
    [InlineData("SCOOP_CACHE", @"{0}\a[1]")]
    [InlineData("SCOOP_CACHE", @"{0}\d``e")]
    [InlineData("SCOOP_CACHE", @"{0}\c*")]
    [InlineData("SCOOP_CACHE", @"{0}\c?")]
    [InlineData("SCOOP", @"{0}\x`y")]
    [InlineData("SCOOP_CACHE", @"\\?\{0}")]
    [InlineData("SCOOP", "scoop")]
    [InlineData("XDG_CONFIG_HOME", ".config")]
    public async Task AVariableScoopCouldReadAsAnotherFolderOffersNothing(string variable, string value)
    {
        _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);
        _scoop.Environment.WithEnvironmentVariable(variable, string.Format(null, value, _scoop.Elsewhere("c")));

        await AssertOffersNothing();
    }

    /// <summary>
    /// <c>$CACHE</c> is the populated default cache, so a file read where Scoop would ignore it, or
    /// fail to read it, plans rather than passing by naming a folder that is not there.
    /// </summary>
    [Theory]
    [InlineData("""{ "cache_path": "cache" }""")]
    [InlineData("""{ "root_path": "scoop" }""")]
    [InlineData("""{ "cache_path": 5 }""")]
    [InlineData("""{ "cache_path": $CACHE, "CACHE_PATH": $CACHE }""")]
    [InlineData("""{ "alias": { "x": "a", "X": "b" } }""")]
    [InlineData("{ \"cache_path\": $CACHE /* a comment PowerShell 7 accepts */ }")]
    [InlineData("[]")]
    public async Task ASettingsFileScoopCouldReadDifferentlyOffersNothing(string json)
    {
        _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);
        _scoop.WriteText(_scoop.UserConfig, json.Replace("$CACHE", Json(_scoop.DefaultCache), StringComparison.Ordinal));

        await AssertOffersNothing();
    }

    /// <summary>A setting left empty or null is one Scoop's <c>Where-Object { $_ }</c> passes over.</summary>
    [Theory]
    [InlineData("""{ "cache_path": "" }""")]
    [InlineData("""{ "cache_path": null, "root_path": null }""")]
    public async Task AnEmptySettingFallsThroughToTheDefault(string json)
    {
        _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);
        _scoop.WriteText(_scoop.UserConfig, json);

        Assert.Equal([_scoop.DefaultCache], ScoopFixture.Step(await _scoop.Provider().PlanAsync()).MeasuredPaths);
    }

    /// <summary>
    /// A root or cache somewhere no tool's folder may be is not examined as Scoop's, wherever the value
    /// came from. The download sits where Scoop would look, so a value taken at its word would plan.
    /// </summary>
    [Theory]
    [InlineData("variable", "SCOOP", "profile")]
    [InlineData("variable", "SCOOP_CACHE", "documents")]
    [InlineData("setting", "root_path", "profile")]
    [InlineData("setting", "cache_path", "documents")]
    public async Task AFolderNoToolMayOwnOffersNothing(string source, string name, string folder)
    {
        _scoop.Install();
        var named = folder == "profile" ? _scoop.Environment.UserProfile : Path.Combine(_scoop.Environment.UserProfile, "Documents");
        _scoop.File(name is "SCOOP" or "root_path" ? Path.Combine(named, "cache") : named, ScoopFixture.Download);

        if (source == "variable")
        {
            _scoop.Environment.WithEnvironmentVariable(name, named);
        }
        else
        {
            _scoop.WriteText(_scoop.UserConfig, $$"""{ "{{name}}": {{Json(named)}} }""");
        }

        var plan = await AssertOffersNothing();
        Assert.Contains(plan.Notes, note => note.Message.Contains(named, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A root that is the profile claims only the folders Scoop keeps there, never the whole profile,
    /// and the settings stay refused.
    /// </summary>
    [Fact]
    public async Task ADeclinedRootIsDeclaredSparingly()
    {
        _scoop.Install();
        var profile = _scoop.Environment.UserProfile;
        _scoop.Environment.WithEnvironmentVariable("SCOOP", profile);

        var roots = await _scoop.Provider().DiscoverToolRootsAsync();
        var atProfile = Assert.Single(roots, root => root.Path.Equals(profile, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ToolRootClaim.NamedEntries, atProfile.Claim);
        Assert.False(atProfile.Recognises(new ToolRootChild("apps", ChildKind.Folder)));
        Assert.False(atProfile.Recognises(new ToolRootChild("persist", ChildKind.Folder)));
        Assert.True(atProfile.Recognises(new ToolRootChild("Documents", ChildKind.Folder)));
        Assert.Contains(roots, root => root.Path.Equals(_scoop.UserConfig, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A cache that is Documents is not declared at all, because a root there would recognise the
    /// downloads and so lift other refusals there, while Scoop's root is still refused whole.
    /// </summary>
    [Fact]
    public async Task ADeclinedCacheIsNotDeclared()
    {
        _scoop.Install();
        var documents = Path.Combine(_scoop.Environment.UserProfile, "Documents");
        _scoop.Environment.WithEnvironmentVariable("SCOOP_CACHE", documents);

        var roots = await _scoop.Provider().DiscoverToolRootsAsync();

        Assert.DoesNotContain(roots, root => root.Path.Equals(documents, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roots, root => root.Path.Equals(_scoop.DefaultRoot, StringComparison.OrdinalIgnoreCase)
            && root.Claim == ToolRootClaim.WholeFolder
            && !root.Recognises(new ToolRootChild("apps", ChildKind.Folder)));
    }

    [Fact]
    public async Task ARefusedSettingsFileOffersNothing()
    {
        _scoop.Install();
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);
        _scoop.WriteText(_scoop.UserConfig, "{}");

        using (DeniedDirectory.WithUnreadableFile(_scoop.UserConfig))
        {
            var plan = await AssertOffersNothing();
            Assert.Contains(plan.Notes, note => note.Message.Contains("Windows would not say", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A <c>PATH</c> that lists <c>.PS1</c> ahead of <c>.CMD</c> finds <c>scoop.ps1</c>, which no
    /// runner starts as a program, so the step starts the <c>scoop.cmd</c> beside it.
    /// </summary>
    [Fact]
    public async Task StartsTheCmdShimBesideWhateverThePathSearchFound()
    {
        var shim = _scoop.Install(found: Path.Combine(_scoop.DefaultRoot, "shims", "scoop.ps1"));
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);

        Assert.Equal(shim, ScoopFixture.Step(await _scoop.Provider().PlanAsync()).FileName);
    }

    [Fact]
    public async Task AScoopWithNoCmdShimBesideItOffersNothing()
    {
        _scoop.Install();
        _scoop.Environment.WithExecutable("scoop", _scoop.Elsewhere("bin", "scoop.ps1"));
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);

        await AssertOffersNothing();
    }

    [Fact]
    public async Task AShimThatDoesNotRunAnInstalledScoopOffersNothing()
    {
        var shim = _scoop.Install();
        _scoop.WriteText(shim, $"@rem {_scoop.Elsewhere("scoop.ps1")}\r\n@echo off");
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);

        await AssertOffersNothing();
    }

    /// <summary>The planner invalidates before a rescan, and the variable moved in between.</summary>
    [Fact]
    public async Task LooksAgainAfterInvalidationBecauseTheCacheCanMove()
    {
        _scoop.Install();
        var moved = _scoop.Elsewhere("moved-cache");
        _scoop.File(_scoop.DefaultCache, ScoopFixture.Download);
        _scoop.File(moved, ScoopFixture.Download);
        var provider = _scoop.Provider();

        Assert.Equal([_scoop.DefaultCache], ScoopFixture.Step(await provider.PlanAsync()).MeasuredPaths);

        _scoop.Environment.WithEnvironmentVariable("SCOOP_CACHE", moved);
        provider.InvalidateCaches();

        Assert.Equal([moved], ScoopFixture.Step(await provider.PlanAsync()).MeasuredPaths);
    }

    private async Task<CleanupPlan> AssertOffersNothing()
    {
        var plan = await _scoop.Provider().PlanAsync();

        Assert.Empty(plan.Steps);
        Assert.True(plan.WasNotExamined);
        Assert.Empty(_scoop.Runner.Invocations);

        return plan;
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
