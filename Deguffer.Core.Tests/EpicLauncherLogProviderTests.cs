using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The launcher's logs and crash reports sit in the same folder as its settings, its cloud saves and
/// the store's browser data, so the whole safety argument is which two children of that folder are
/// recognised. These are mostly negative tests: that nothing else in the listing is ever a target,
/// that an unrecognised child lands at Tier 4, and that a link is never deleted through.
/// </summary>
public sealed class EpicLauncherLogProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public EpicLauncherLogProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private EpicLauncherLogProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string Saved => Path.Combine(_environment.LocalAppData, "EpicGamesLauncher", "Saved");

    /// <summary>Create a directory holding one file, so it measures as non-empty.</summary>
    private static string CreateDirectory(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[bytes]);
        return path;
    }

    /// <summary>The settings, saved state and browser folder that share the directory listing.</summary>
    private string[] AddLauncherState()
    {
        string[] names = ["Config", "Data", "Saves", "UserVaultSettings", "webcache_4430"];

        return [.. names.Select(name => CreateDirectory(Path.Combine(Saved, name)))];
    }

    [Fact]
    public async Task ReportsNotPresentWhenTheLauncherHasNoFolder()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.IsEmpty);
        Assert.Equal(0, plan.EstimatedBytes);
    }

    /// <summary>
    /// The folder existing is not evidence that a log inside it does. Every machine that has opened
    /// the launcher has one, so reading that as a hit would offer a row the plan has nothing to say
    /// about.
    /// </summary>
    [Fact]
    public async Task ReportsNotPresentWhenTheLauncherHasWrittenNoLogOrCrashReport()
    {
        AddLauncherState();

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task TargetsTheLogsAndTheCrashReports()
    {
        var crashes = CreateDirectory(Path.Combine(Saved, "Crashes"));
        var logs = CreateDirectory(Path.Combine(Saved, "Logs"));

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Contains(crashes, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(logs, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, plan.Steps.Count);
        Assert.True(plan.EstimatedBytes > 0);
    }

    /// <summary>
    /// §3's Tier 3, and the row is never pre-selected because of it. A crash report is the record of
    /// an event that will not happen again to order, so §7 holds the confirmation to the stricter
    /// answer rather than ticking the box for the user.
    /// </summary>
    [Fact]
    public void IsTierThreeAndDeclaresBothChildrenThere()
    {
        var provider = CreateProvider();

        Assert.Equal(SafetyTier.UserData, provider.Tier);
        Assert.False(provider.Tier.IsPreSelectedByDefault());

        Assert.All(
            EpicLauncherSaved.Diagnostics.DisposableNames,
            name => Assert.Equal(provider.Tier, EpicLauncherSaved.Diagnostics.Classify(name).Tier));
    }

    /// <summary>
    /// §7's age column. A log is appended to, which moves the file and leaves the parent alone, so a
    /// timestamp read from the directory alone would report a log written this minute as months old
    /// — beside a row whose loss is permanent.
    /// </summary>
    [Fact]
    public async Task EachRowCarriesTheNewestWriteInsideIt()
    {
        var logs = CreateDirectory(Path.Combine(Saved, "Logs"));
        var log = Path.Combine(logs, "entry.bin");

        // The folder and its one entry both pushed back, then the entry appended to. NTFS moves a
        // directory's own timestamp when an entry is added, removed or renamed, and leaves it alone
        // when an entry's contents change — so this is the case the two rules answer differently.
        TempDirectory.Age(log, TimeSpan.FromDays(400));
        Directory.SetLastWriteTimeUtc(LongPath.Extended(logs), DateTime.UtcNow.AddDays(-400));

        File.WriteAllBytes(log, new byte[8192]);

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps);

        Assert.NotNull(step.LastWritten);
        Assert.True(
            step.LastWritten > DateTime.UtcNow.AddDays(-1),
            "the age came from the folder rather than from the log being written inside it.");
    }

    /// <summary>
    /// §5.6. The settings, the cloud saves and the store's browser folder sit in the same directory
    /// listing as the two that go. Asserting that the logs went is half a test.
    /// </summary>
    [Fact]
    public async Task NeverTargetsTheLauncherFolderOrAnythingElseInIt()
    {
        var state = AddLauncherState();
        CreateDirectory(Path.Combine(Saved, "Crashes"));
        CreateDirectory(Path.Combine(Saved, "Logs"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        string[] survivors = [Saved, .. state];

        foreach (var path in survivors)
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

            // Not merely absent from the plan — asserted to survive (§5.6).
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);

        foreach (var path in survivors)
        {
            Assert.True(Directory.Exists(path), $"'{path}' was removed alongside the logs.");
        }

        Assert.False(Directory.Exists(Path.Combine(Saved, "Crashes")));
        Assert.False(Directory.Exists(Path.Combine(Saved, "Logs")));
    }

    /// <summary>
    /// §5.2's dangerous direction is an unknown thing treated as safe, so a name the table does not
    /// carry lands at Tier 4 rather than being guessed at.
    /// </summary>
    [Theory]
    [InlineData("Config")]
    [InlineData("UserVaultSettings")]
    [InlineData("something-unrecognised")]
    public async Task AnUnrecognisedChildOfTheLauncherFolderIsNeverTargeted(string name)
    {
        CreateDirectory(Path.Combine(Saved, "Logs"));
        var sibling = CreateDirectory(Path.Combine(Saved, name));

        Assert.Equal(SafetyTier.DoNotTouch, EpicLauncherSaved.Classify(name).Tier);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(sibling, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(sibling), $"'{name}' was removed alongside the logs.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// A web cache folder is spared with a sentence of its own rather than with the generic "not
    /// recognised" one. That wording would be misleading here, because the folder really is left
    /// standing while the web cache row really is removing caches from inside it.
    /// </summary>
    [Fact]
    public void AWebCacheFolderIsSparedWithItsOwnReason()
    {
        var classification = EpicLauncherSaved.Classify("webcache_4430");

        Assert.Equal(SafetyTier.DoNotTouch, classification.Tier);
        Assert.Contains("browser folder", classification.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not a recognised", classification.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The path down to <c>Saved</c> is built from <c>%LOCALAPPDATA%</c> plus two constants rather
    /// than enumerated, so nothing on the way down has been through a filter that separates links
    /// out. A junction at any segment redirects the deletion while every §5.6 survivor named below
    /// resolves through the same link and passes.
    /// </summary>
    [Theory]
    [InlineData("EpicGamesLauncher")]
    [InlineData(@"EpicGamesLauncher\Saved")]
    public async Task AJunctionAnywhereOnThePathToTheLauncherFolderIsNeverLookedThrough(string relative)
    {
        var outside = _temp.CreateDirectory("elsewhere");
        var bystander = CreateDirectory(Path.Combine(outside, "Logs"));

        var link = Path.Combine(_environment.LocalAppData, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, outside);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(bystander, "entry.bin")),
            "planning looked through a link and deleted the far side.");
    }

    /// <summary>
    /// A junctioned log folder is a child the user can see, so a plan that neither offers it nor
    /// mentions it disagrees with the folder. Dropping it silently would also make the row read as
    /// clear, since presence resolves through the link.
    /// </summary>
    [Fact]
    public async Task AJunctionedLogFolderIsNamedRatherThanDroppedSilently()
    {
        var outside = CreateDirectory(Path.Combine(_temp.Path, "elsewhere"));

        Directory.CreateDirectory(Saved);
        Directory.CreateSymbolicLink(Path.Combine(Saved, "Logs"), outside);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("Logs", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(outside, "entry.bin")),
            "a junctioned log folder was deleted through.");
    }

    /// <summary>
    /// §7.1's second deletion route reads §5.2 out of this declaration rather than restating it. It
    /// is the same root the web cache provider declares, so Explore answers the same way whichever
    /// of the two it reads.
    /// </summary>
    [Fact]
    public void DeclaresTheSameLauncherFolderRootAsTheWebCacheProvider()
    {
        var root = Assert.Single(CreateProvider().ToolRoots);

        Assert.Equal(Saved, root.Path, StringComparer.OrdinalIgnoreCase);
        Assert.True(root.Recognises("Crashes"));
        Assert.True(root.Recognises("Logs"));
        Assert.False(root.Recognises("Config"));
        Assert.False(root.Recognises("webcache_4430"));

        var fromWebCache = new EpicLauncherWebCacheProvider(_environment).ToolRoots.Single(r =>
            r.Path.Equals(Saved, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(root.Reason, fromWebCache.Reason, StringComparer.Ordinal);
        Assert.Equal(root.Recognises("Crashes"), fromWebCache.Recognises("Crashes"));
        Assert.Equal(root.Recognises("webcache_4430"), fromWebCache.Recognises("webcache_4430"));
    }
}
