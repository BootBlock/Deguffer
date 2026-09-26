using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The logs Claude Code keeps for each MCP server it runs: Tier 3, one folder per server per project, in
/// a cache folder beside project folders named from the user's own paths.
///
/// <para>Mostly negative tests, for the reason every provider over a shared folder has them: the folder
/// a server's log sits in is a sibling of whatever else Claude Code keeps per project, and that is
/// exactly when an over-broad rule takes both.</para>
/// </summary>
public sealed class ClaudeCodeMcpLogProviderTests : IDisposable
{
    private const string ProjectName = "C--Users-testuser-src-example";

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public ClaudeCodeMcpLogProviderTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private string Cache => Path.Combine(_environment.LocalAppData, "claude-cli-nodejs", "Cache");

    private string Project(string name = ProjectName) => Path.Combine(Cache, name);

    private ClaudeCodeMcpLogProvider CreateProvider() =>
        new(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);

    /// <summary>
    /// A server's log folder holding one run's log. Aged as a whole, the file first and the folder last,
    /// because a log folder is dated by the newest write one level down and a new file would date it now.
    /// </summary>
    private string ServerLog(string server, string project = ProjectName, TimeSpan? age = null)
    {
        var folder = Path.Combine(Project(project), ClaudeCodeMcpLogProvider.LogFolderPrefix + server);
        Directory.CreateDirectory(folder);

        var log = Path.Combine(folder, "2026-01-01T10-00-00-000Z.jsonl");
        File.WriteAllBytes(log, new byte[1024]);

        if (age is { } by)
        {
            TempDirectory.Age(log, by);
        }

        ClaudeCodeFixture.AgeFolder(folder, age);
        return folder;
    }

    [Fact]
    public async Task ReportsNotPresentWhereNoServerHasALog()
    {
        Assert.False(await CreateProvider().IsPresentAsync());

        Directory.CreateDirectory(Path.Combine(Project(), "errors"));

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    [Fact]
    public async Task PlansEveryServersLogFolderAndNothingElse()
    {
        var first = ServerLog("filesystem");
        var second = ServerLog("browser", "C--Users-testuser-src-another");

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(
            new[] { first, second }.Order(StringComparer.OrdinalIgnoreCase),
            plan.TargetedPaths.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(SafetyTier.UserData, plan.Tier);
        Assert.All(plan.Steps, step => Assert.NotNull(step.LastWritten));
        Assert.All(plan.Steps, step => Assert.False(((Execution.DeleteStep)step).IsLeftover));
    }

    /// <summary>§5.2 inside a project's folder, including names one character away from a server's log.</summary>
    [Theory]
    [InlineData("errors")]
    [InlineData("mcp-logs-")]
    [InlineData("mcp-log-typo")]
    [InlineData("logs")]
    public async Task AnythingElseInAProjectsFolderIsTier4AndSurvives(string name)
    {
        ServerLog("filesystem");

        var sibling = Path.Combine(Project(), name);
        Directory.CreateDirectory(sibling);
        File.WriteAllBytes(Path.Combine(sibling, "entry.jsonl"), new byte[256]);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.DoesNotContain(sibling, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(sibling, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(sibling), $"{name} was removed beside a server's log");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task TheCacheAndEveryProjectFolderSurviveTheClean()
    {
        var log = ServerLog("filesystem");
        var project = Project();
        var tool = Path.GetDirectoryName(Cache)!;

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        foreach (var path in new[] { tool, Cache, project })
        {
            Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && p.PresenceBefore is PathPresence.Present);
        }

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(log));
        Assert.True(Directory.Exists(project), "the project's folder was removed with its log");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    [Fact]
    public async Task ExploreMayRemoveAServersLogAndNothingAboveOrBesideIt()
    {
        var log = ServerLog("filesystem");
        var errors = Path.Combine(Project(), "errors");
        Directory.CreateDirectory(errors);

        var provider = CreateProvider();
        await provider.PlanAsync();

        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

        Assert.True(policy.MayRemove(log).IsAllowed);

        var toolFolder = Path.GetDirectoryName(Cache)!;
        var besideTheCache = Path.Combine(toolFolder, "config");
        Directory.CreateDirectory(besideTheCache);

        foreach (var refused in new[] { toolFolder, besideTheCache, Cache, Project(), errors })
        {
            Assert.False(policy.MayRemove(refused).IsAllowed, $"Explore would remove {refused}");
        }
    }

    /// <summary>
    /// A cache folder Windows will not describe is declared all the same. The plan names it as
    /// unreached, and the declaration dropped it, so Explore offered a server's log and the folder
    /// holding it about a folder the Storage page said nothing was ruled out in.
    /// </summary>
    [Fact]
    public async Task ExploreRefusesAllOfACacheFolderWindowsWillNotDescribe()
    {
        var log = ServerLog("filesystem");

        using var denied = DeniedDirectory.WithUnreadableAttributes(Cache);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);

        var policy = new ExploreActionPolicy([], provider.ToolRoots, new FakeVolumeInventory());

        foreach (var refused in new[] { Path.GetDirectoryName(Cache)!, Cache, Project(), log })
        {
            Assert.False(policy.MayRemove(refused).IsAllowed, $"Explore would remove {refused}");
        }
    }

    [Fact]
    public async Task ALinkedLogFolderIsNamedAndNeverFollowed()
    {
        var outside = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(outside);
        var bystander = Path.Combine(outside, "precious.jsonl");
        File.WriteAllBytes(bystander, new byte[64]);

        Directory.CreateDirectory(Project());
        SymbolicLink.ToDirectory(Path.Combine(Project(), ClaudeCodeMcpLogProvider.LogFolderPrefix + "linked"), outside);

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Empty(plan.TargetedPaths);
        Assert.True(plan.WasNotExamined);
        Assert.Contains(plan.Notes, n => n.Message.Contains("link", StringComparison.Ordinal));

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(bystander), "a file was deleted through a linked log folder");
    }

    [Fact]
    public async Task AProjectFolderThatWillNotBeListedIsSaidSoRatherThanReportedAsEmpty()
    {
        ServerLog("filesystem");

        using var denied = new DeniedDirectory(Project());

        var provider = CreateProvider();

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.True(plan.HasUnreadableRoot);
        Assert.Empty(plan.TargetedPaths);
        Assert.Contains(plan.Notes, n => n.Severity == Execution.PlanNoteSeverity.Warning);
    }

    [Fact]
    public async Task EachLogFolderCarriesWhenItWasLastWrittenTo()
    {
        var log = ServerLog("filesystem", age: TimeSpan.FromDays(40));

        var plan = await CreateProvider().PlanAsync();

        var step = Assert.Single(plan.Steps);

        Assert.Equal(log, ((Execution.DeleteStep)step).Path);
        Assert.InRange(step.LastWritten!.Value, DateTime.UtcNow.AddDays(-41), DateTime.UtcNow.AddDays(-39));
    }

    /// <summary>
    /// Claude Code's folder variable moves its own folder and not this one, which the tool it runs puts
    /// under the per-user application data folder whatever that variable says.
    /// </summary>
    [Fact]
    public async Task IsNotMovedByClaudeCodesFolderVariable()
    {
        var log = ServerLog("filesystem");
        _environment.WithEnvironmentVariable(ClaudeCodeHome.ConfigDirectoryVariable, Path.Combine(_temp.Path, "moved"));

        Assert.Equal([log], (await CreateProvider().PlanAsync()).TargetedPaths);
    }

    [Fact]
    public async Task TheCacheIsReadOncePerPassAndAgainAfterInvalidation()
    {
        var provider = CreateProvider();
        Directory.CreateDirectory(Project());

        Assert.Empty((await provider.PlanAsync()).TargetedPaths);

        var log = ServerLog("filesystem");

        Assert.Empty((await provider.PlanAsync()).TargetedPaths);

        provider.InvalidateCaches();

        Assert.Equal([log], (await provider.PlanAsync()).TargetedPaths);
    }
}
