using System.Globalization;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;
using static Deguffer.Testing.ClaudeCodeFixture;

namespace Deguffer.Core.Tests;

/// <summary>
/// The per-file work that blocks on the disk, run on <see cref="ListWorkers"/> rather than the thread
/// pool: a removal's deletions, a refusal check's opens and the reads of Claude Code's conversations.
/// On the pool each would run below its bound until the pool grew, and hold the threads the rest of
/// the process's pool work waits for.
///
/// <para>Each asserts on the items themselves, not on a crew: the walk before a removal already runs
/// on a crew, so a crew being made off the pool says nothing about the deletions after it.</para>
/// </summary>
public sealed class FileWorkRunsOffThePoolTests : IDisposable
{
    /// <summary>Enough items, each held long enough, that a loop on the pool hands some to pool threads.</summary>
    private const int Folders = 24;

    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(2);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ARemovalDeletesNoFileOrFolderOnThePool()
    {
        var folder = _temp.CreateDirectory("profile", "cache");
        for (var index = 0; index < Folders; index++)
        {
            _temp.CreateFile(16, "profile", "cache", $"f{index}", "a.bin");
            _temp.CreateFile(16, "profile", "cache", $"f{index}", "b.bin");
        }

        var sibling = _temp.CreateFile(16, "profile", "kept.txt");
        var fs = new PoolWatchingFileSystem(WindowsFileSystem.Default, Hold);

        var outcome = await DirectoryRemover.RemoveAsync(folder, fileSystem: fs);

        Assert.True(outcome.RootRemoved);
        // Every file, every folder inside, and the folder itself.
        Assert.Equal((2 * Folders) + Folders + 1, fs.Calls);
        Assert.Equal(0, fs.OnThePool);
        // §5.6: what was beside the folder is still there.
        Assert.True(LongPath.FileExists(sibling));
    }

    /// <summary>Asked on a thread of its own, as <see cref="CleanupProviderBase.PlanAsync"/> asks it.</summary>
    [Fact]
    public async Task ARefusalCheckOpensNoFileOnThePool()
    {
        var step = _temp.CreateDirectory("temp");
        var place = _temp.CreateDirectory("temp", "profile");
        for (var index = 0; index < 2 * Folders; index++)
        {
            _temp.CreateFile(16, "temp", "profile", $"{index}.bin");
        }

        var fs = new PoolWatchingFileSystem(WindowsFileSystem.Default, Hold);

        var finding = await WorkerThreads.Shared.RunAsync(
            () => RefusalCheck.Of(new ClearDirectoryStep(step, "Scratch"), [place], MinimumAge.Off, fs, default),
            default);

        Assert.True(finding.Refused.IsEmpty);
        Assert.Equal(2 * Folders, fs.Calls);
        Assert.Equal(0, fs.OnThePool);
        // The check opens files and deletes nothing.
        Assert.Equal(2 * Folders, Directory.EnumerateFiles(place).Count());
    }

    /// <summary>
    /// Planned from the pool, as a preview plans. The scanner is a fake that walks nothing, so the only
    /// crew is the one reading the conversations.
    /// </summary>
    [Fact]
    public async Task ClaudeCodesConversationsAreReadOffThePool()
    {
        var environment = new FakeUserEnvironment(_temp.Path);
        var claude = new ClaudeCodeFixture(environment);
        var sessions = Enumerable.Range(1, 8)
            .Select(index => string.Create(CultureInfo.InvariantCulture, $"{index:D8}-0000-4000-8000-{index:D12}"))
            .ToList();

        foreach (var session in sessions)
        {
            claude.Conversation(session, age: Old);
        }

        var provider = new ClaudeCodeConversationProvider(
            environment,
            runner: new FakeProcessRunner(),
            inspector: FakeProcessInspector.NothingRunning,
            scanner: new FakeDirectoryScanner());
        CleanupPlan? plan = null;

        var made = await CrewThreads.MadeBy(async () => plan = await Task.Run(() => provider.PlanAsync()));

        CrewThreads.AssertOffThePool(made);
        Assert.Equal(sessions.Count, plan!.TargetedPaths.Count);
    }
}
