using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Tier 3 half of what a Code - OSS editor keeps: the log of every session it has run, and the
/// crash reporter's database. It works in the same folder as
/// <see cref="VsCodeCacheProviderTests"/>'s subject and next to the same <c>User</c> tree, so the
/// negative assertions matter here for the same reason — with the addition that everything this one
/// removes is gone for good.
/// </summary>
public sealed class VsCodeLogProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public VsCodeLogProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private VsCodeLogProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    private string CreateEditor(string name = "Code")
    {
        var path = Path.Combine(_environment.RoamingAppData, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"<REDACTED>\"}}");
        CreateFile(Path.Combine(path, "User", "globalStorage", "state.vscdb"));
        return path;
    }

    private static string CreateDirectory(string path, int bytes = 4096)
    {
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "entry.bin"), new byte[bytes]);
        return path;
    }

    private static string CreateFile(string path, int bytes = 64)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task ReportsNotPresentWhenNoEditorHasWrittenAnything()
    {
        var provider = CreateProvider();

        Assert.False(await provider.IsPresentAsync());
        Assert.True((await provider.PlanAsync()).IsEmpty);
    }

    /// <summary>
    /// A user-data folder with neither a log nor a crash database in it is not a source. An editor
    /// installed and never run is exactly that.
    /// </summary>
    [Fact]
    public async Task AUserDataFolderWithNoRecordInItIsNotPresence()
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "CachedData"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansTheSessionLogsAndTheCrashDatabase()
    {
        var editor = CreateEditor();
        var logs = CreateDirectory(Path.Combine(editor, "logs", "20260101T090000"));
        var crashpad = CreateDirectory(Path.Combine(editor, "Crashpad", "reports"));

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { Path.Combine(editor, "logs"), Path.Combine(editor, "Crashpad") }
                .Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(SafetyTier.UserData, plan.Tier);
        Assert.True(plan.EstimatedBytes > 0);

        // Named so the fixtures above are not merely decorative: both trees really are inside the
        // two targets.
        Assert.True(Directory.Exists(logs) && Directory.Exists(crashpad));
    }

    /// <summary>
    /// §5.2's dangerous direction, and the case unique to this provider: the caches
    /// <see cref="VsCodeCacheProvider"/> removes are Tier 4 <em>here</em>. They are that provider's
    /// to offer, under its own tier and its own sentence, and a Tier 3 confirmation is not the one a
    /// user should be giving for a regenerable cache.
    /// </summary>
    [Theory]
    [InlineData("CachedData")]
    [InlineData("CachedExtensionVSIXs")]
    [InlineData("WebStorage")]
    [InlineData("User")]
    [InlineData("Backups")]
    [InlineData("logs-old")]
    public async Task AnUnrecognisedSiblingIsTier4AndIsAssertedToSurvive(string name)
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "logs", "20260101T090000"));
        var sibling = CreateDirectory(Path.Combine(editor, name));

        Assert.Equal(SafetyTier.DoNotTouch, VsCodeLogProvider.FolderChildren.Classify(name).Tier);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(sibling, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p =>
            p.Path.Equals(sibling, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(sibling), $"{name} was removed alongside the logs.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The <c>User</c> tree again, named in full. This provider never enters it, so nothing would
    /// classify it, and §4.3 makes it the most valuable directory either of the two providers over
    /// this folder works beside.
    /// </summary>
    [Fact]
    public async Task TheUserTreeIsAssertedToSurviveEvenThoughItIsNeverClassified()
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "logs", "20260101T090000"));

        var workspaces = CreateDirectory(Path.Combine(editor, "User", "workspaceStorage"));
        var history = CreateDirectory(Path.Combine(editor, "User", "History"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var path in new[] { editor, workspaces, history })
        {
            Assert.Contains(plan.ProtectedPaths, p =>
                p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.ExistedBefore);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(workspaces));
        Assert.True(Directory.Exists(history));
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// Both of these are Tier 3, and the planner's own gate names every provider above Tier 1
    /// individually. This is the same claim read from the table rather than from the registry, so a
    /// child that arrives here at Tier 1 by mistake would be pre-selected under a sentence saying
    /// nothing is lost.
    /// </summary>
    [Fact]
    public void EveryDeclaredChildIsTheTierTheProviderClaims()
    {
        var provider = CreateProvider();

        foreach (var name in VsCodeLogProvider.FolderChildren.DisposableNames)
        {
            Assert.Equal(provider.Tier, VsCodeLogProvider.FolderChildren.Classify(name).Tier);
        }
    }

    [Fact]
    public void TheTableDeclaresTheTwoRecordDirectoriesAndNoOthers()
    {
        Assert.Equal(
            ["Crashpad", "logs"],
            VsCodeLogProvider.FolderChildren.DisposableNames.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A junctioned <c>logs</c> is a child the user can see, so it is named rather than dropped —
    /// and never followed, because what it points at was never classified.
    /// </summary>
    [Fact]
    public async Task AJunctionedRecordDirectoryIsNamedRatherThanDeletedThrough()
    {
        var outside = CreateDirectory(Path.Combine(_temp.Path, "elsewhere"));

        var editor = CreateEditor();
        Directory.CreateSymbolicLink(Path.Combine(editor, "logs"), outside);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n =>
            n.Message.Contains("logs", StringComparison.Ordinal) &&
            n.Message.Contains("link", StringComparison.Ordinal));
        Assert.True(plan.WasNotExamined);

        await provider.ExecuteAsync(plan);

        Assert.True(
            File.Exists(Path.Combine(outside, "entry.bin")), "a junctioned log folder was deleted through.");
    }

    /// <summary>
    /// The folder is found by name and then has to be listed to classify its children, and those
    /// are separate rights. Without this the provider reports that the editor has written no logs —
    /// contradicting the probe that just found them.
    /// </summary>
    [Fact]
    public async Task AUserDataFolderThatWillNotBeListedIsSaidSoRatherThanReportedAsEmpty()
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "logs", "20260101T090000"));

        using var denied = new DeniedDirectory(editor);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains(editor, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Notes, n =>
            n.Message.Contains("has written no log", StringComparison.Ordinal));
        Assert.Empty(plan.TargetedPaths);
    }

    [Fact]
    public async Task ExecutingRemovesTheRecordsAndLeavesTheFolderStanding()
    {
        var editor = CreateEditor();
        var logs = CreateDirectory(Path.Combine(editor, "logs", "20260101T090000"));
        var crashpad = CreateDirectory(Path.Combine(editor, "Crashpad", "reports"));
        var caches = CreateDirectory(Path.Combine(editor, "CachedData"));

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();
        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed > 0);
        Assert.False(Directory.Exists(logs));
        Assert.False(Directory.Exists(crashpad));

        Assert.True(Directory.Exists(editor));
        Assert.True(Directory.Exists(caches), "the other provider's cache was removed by this one.");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// §7 gives the age column to per-workspace and per-project data. Each of these is one whole
    /// record store spanning every session the editor has ever run, so a single timestamp on it
    /// would be a number with nothing to mean — and one the user might act on.
    /// </summary>
    [Fact]
    public async Task NoStepCarriesAnAgeBecauseTheseAreWholeStores()
    {
        var editor = CreateEditor();
        CreateDirectory(Path.Combine(editor, "logs", "20260101T090000"));
        CreateDirectory(Path.Combine(editor, "Crashpad", "reports"));

        var plan = await CreateProvider().PlanAsync();

        Assert.NotEmpty(plan.Steps);
        Assert.All(plan.Steps, step => Assert.Null(step.LastWritten));
    }
}
