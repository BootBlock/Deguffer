using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Playwright is the path-based Tier 2 case. The rules worth proving are §5.2's — that a versioned
/// name is recognised only when it is both a known browser and a revision, and that everything else
/// keeps Tier 4 — and §5.6's, that the registry Playwright uses for its own housekeeping survives.
/// </summary>
public sealed class PlaywrightBrowsersProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public PlaywrightBrowsersProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private PlaywrightBrowsersProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>Create the default browser root with the given children, each holding a payload.</summary>
    private string CreateRoot(params string[] children)
    {
        var root = Path.Combine(_environment.LocalAppData, "ms-playwright");
        Directory.CreateDirectory(root);

        foreach (var child in children)
        {
            var path = Path.Combine(root, child);
            Directory.CreateDirectory(path);
            File.WriteAllBytes(Path.Combine(path, "payload.bin"), new byte[4096]);
        }

        return root;
    }

    [Fact]
    public async Task ReportsNotPresentWhenPlaywrightNeverDownloadedABrowser()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    [Fact]
    public async Task TargetsRecognisedBrowserBuilds()
    {
        var root = CreateRoot("chromium-1228", "firefox-1532", "ffmpeg-1011");

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal(3, plan.Steps.Count);
        Assert.Contains(Path.Combine(root, "chromium-1228"), plan.TargetedPaths);
        Assert.Contains(Path.Combine(root, "firefox-1532"), plan.TargetedPaths);
        Assert.Contains(Path.Combine(root, "ffmpeg-1011"), plan.TargetedPaths);
    }

    /// <summary>
    /// The headless shell and tip-of-tree builds carry underscores and extra hyphens in the part
    /// before the revision, which a naive "split on the last hyphen" rule would mis-handle.
    /// </summary>
    [Theory]
    [InlineData("chromium_headless_shell-1228")]
    [InlineData("chromium-tip-of-tree-1229")]
    [InlineData("winldd-1007")]
    [InlineData("webkit-2210")]
    public async Task RecognisesTheAwkwardlyNamedBuilds(string name)
    {
        var root = CreateRoot(name);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(Path.Combine(root, name), plan.TargetedPaths);
    }

    /// <summary>
    /// §5.2's dangerous direction: a child this provider does not recognise must land in Tier 4 and
    /// stay out of the plan, however cache-like it looks.
    /// </summary>
    [Theory]
    [InlineData(".links")]                    // Playwright's own registry.
    [InlineData("chromium")]                  // A known name, but no revision.
    [InlineData("chromium-")]                 // Revision missing after the separator.
    [InlineData("chromium-abc")]              // Revision not numeric.
    [InlineData("chromium-1228-backup")]      // Something a person made, not Playwright.
    [InlineData("my-notes-2024")]             // Unrelated directory that happens to end in digits.
    [InlineData("Xchromium-1228")]            // Prefixed: must not match unanchored.
    public async Task LeavesUnrecognisedChildrenAloneAndSaysSo(string name)
    {
        CreateRoot("chromium-1228", name);

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(
            plan.TargetedPaths,
            p => string.Equals(Path.GetFileName(p), name, StringComparison.Ordinal));

        Assert.Contains(plan.Notes, n => n.Message.Contains($"Leaving '{name}' alone", StringComparison.Ordinal));
    }

    /// <summary>
    /// §5.6. The root and the .links registry are the two things a reclaim must never take, and
    /// .links is the subtle one — it looks like cache and drives Playwright's own housekeeping.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheRootOrPlaywrightsOwnRegistry()
    {
        var root = CreateRoot("chromium-1228");
        var links = Path.Combine(root, ".links");
        Directory.CreateDirectory(links);
        File.WriteAllText(Path.Combine(links, "ccbdd79442b392e1537885a7c223b9749312fa2b"), "example");

        var plan = await CreateProvider().PlanAsync();

        Assert.DoesNotContain(root, plan.TargetedPaths);
        Assert.DoesNotContain(links, plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == root && p.ExistedBefore);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == links && p.ExistedBefore);
    }

    /// <summary>
    /// Tier 2, not Tier 1: nothing re-downloads these automatically, so the next test run fails
    /// until somebody reinstalls. That must never be pre-selected for the user.
    /// </summary>
    [Fact]
    public void IsTierTwoSoItIsOfferedButNeverPreSelected()
    {
        var provider = CreateProvider();

        Assert.Equal(SafetyTier.RegenerableWithCost, provider.Tier);
        Assert.True(provider.Tier.IsOfferable());
        Assert.False(provider.Tier.IsPreSelectedByDefault());
    }

    [Fact]
    public async Task HonoursTheEnvironmentVariableThatRelocatesTheBrowserCache()
    {
        var elsewhere = Path.Combine(_temp.Path, "browsers");
        Directory.CreateDirectory(Path.Combine(elsewhere, "chromium-1228"));
        File.WriteAllBytes(Path.Combine(elsewhere, "chromium-1228", "payload.bin"), new byte[4096]);

        _environment.WithEnvironmentVariable(PlaywrightBrowsersProvider.LocationVariable, elsewhere);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());
        Assert.Contains(Path.Combine(elsewhere, "chromium-1228"), (await provider.PlanAsync()).TargetedPaths);
    }

    /// <summary>
    /// "0" is Playwright's sentinel for per-project installs, not a directory. Treating it as a
    /// path would have the provider probe a folder literally named "0".
    /// </summary>
    [Fact]
    public async Task OffersNothingWhenBrowsersLiveInsideEachProject()
    {
        CreateRoot("chromium-1228");
        _environment.WithEnvironmentVariable(PlaywrightBrowsersProvider.LocationVariable, "0");

        var provider = CreateProvider();

        Assert.Null(provider.ResolveRoot());
        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.TargetedPaths);
    }

    /// <summary>
    /// A relative value has no correct reading here: Playwright resolves it against the test
    /// process's directory, and Deguffer is not that process. Resolving it against Deguffer's own
    /// working directory would enumerate — and target — a directory nobody pointed at.
    /// </summary>
    [Theory]
    [InlineData("browsers")]
    [InlineData(@".\browsers")]
    [InlineData(@"..\browsers")]
    public async Task RefusesToGuessWhenTheConfiguredLocationIsNotAFullPath(string configured)
    {
        CreateRoot("chromium-1228");
        _environment.WithEnvironmentVariable(PlaywrightBrowsersProvider.LocationVariable, configured);

        var provider = CreateProvider();

        Assert.Null(provider.ResolveRoot());
        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.TargetedPaths);

        // The default location must not be used as a silent fallback either: the user did set the
        // variable, so falling back would clean a folder they had pointed away from.
        Assert.DoesNotContain(plan.TargetedPaths, p => p.Contains("ms-playwright", StringComparison.Ordinal));
    }

    /// <summary>
    /// §6.3: a browser cache relocated past MAX_PATH must still be measured. This assertion is
    /// weaker on a machine with LongPathsEnabled set.
    /// </summary>
    [Fact]
    public async Task MeasuresABrowserCacheRelocatedBeyondMaxPath()
    {
        var deep = _temp.Path;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('p', 40));
        }

        var browser = Path.Combine(deep, "chromium-1228");
        Assert.True(browser.Length > 260);

        Directory.CreateDirectory(LongPath.Extended(browser));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(browser, "payload.bin")), new byte[4096]);

        _environment.WithEnvironmentVariable(PlaywrightBrowsersProvider.LocationVariable, deep);

        var plan = await CreateProvider().PlanAsync();

        Assert.Contains(browser, plan.TargetedPaths);
        Assert.True(plan.EstimatedBytes > 0, "A browser build past MAX_PATH was measured as empty.");
    }
}
