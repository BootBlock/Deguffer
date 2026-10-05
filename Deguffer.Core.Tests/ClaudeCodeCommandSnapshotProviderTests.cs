using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using static Deguffer.Testing.ClaudeCodeFixture;

namespace Deguffer.Core.Tests;

/// <summary>
/// The snapshots Claude Code keeps of each project it runs commands in: offered once no running session's
/// process can have made one, or once Claude Code has begun to remove it, and never otherwise.
///
/// <para>A process keeps its snapshot after it moves to another session, so a snapshot named for a session
/// nothing lists can still be in use. The tests that matter here make one, and show it kept.</para>
///
/// <para>Everything runs against an invented temporary folder and an invented Claude Code folder through
/// <see cref="FakeUserEnvironment"/> and <see cref="FakeProcessInspector"/>. No test reads a real one.</para>
/// </summary>
public sealed class ClaudeCodeCommandSnapshotProviderTests : IDisposable
{
    private const int SessionProcess = 4242;

    /// <summary>The first two parts of a snapshot's name: hashes Claude Code writes, never read here.</summary>
    private const string Hashes = "11111111111111111111-2222222222222222222";

    /// <summary>When the running session's process started.</summary>
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow - TimeSpan.FromHours(6);

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly ClaudeCodeFixture _claude;
    private int _taken;

    public ClaudeCodeCommandSnapshotProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path);
        _system = new FakeSystemDirectories(_temp.Path);
        _claude = new ClaudeCodeFixture(_environment);
    }

    public void Dispose() => _temp.Dispose();

    private string Snapshots => Path.Combine(
        _environment.TempPath,
        ClaudeCodeCommandSnapshotProvider.TemporaryFolder,
        ClaudeCodeCommandSnapshotProvider.SnapshotFolder);

    private static FakeProcessInspector SessionRunning =>
        FakeProcessInspector.NothingRunning.WithProcess(SessionProcess, ProcessLiveness.Running(Started));

    private ClaudeCodeCommandSnapshotProvider CreateProvider(IProcessInspector? inspector = null) =>
        new(
            _environment,
            new FakeProcessRunner(),
            inspector ?? SessionRunning,
            system: _system,
            liveTrees: FakeLiveTreeInspector.NothingLive);

    /// <summary>A running session, whatever it is called now, whose process started at <see cref="Started"/>.</summary>
    private void SessionStarted(string session = SessionA) => _claude.Registered(SessionProcess, session, Started);

    /// <summary>
    /// A snapshot as Claude Code leaves one, made at <paramref name="created"/>: <c>HEAD</c> and
    /// <c>config</c>, the project's index, and an object. One whose removal began has lost the first two.
    /// </summary>
    private string Snapshot(DateTimeOffset created, bool removalBegun = false)
    {
        var path = Path.Combine(Snapshots, $"{Hashes}-{++_taken:x16}");

        Write(path, "index", 2048);
        Write(path, Path.Combine("objects", "ab", "cdef0123456789abcdef0123456789abcdef01"), 4096);

        if (!removalBegun)
        {
            Write(path, "HEAD", 21);
            Write(path, "config", 99);
        }

        Directory.SetCreationTimeUtc(path, created.UtcDateTime);
        return path;
    }

    private string Write(string folder, string relative, int bytes) =>
        _temp.CreateFile(bytes, [.. Path.GetRelativePath(_temp.Path, Path.Combine(folder, relative)).Split(Path.DirectorySeparatorChar)]);

    /// <summary>Made before the running session started, by more than the row's allowance for the clock.</summary>
    private static DateTimeOffset Before => Started - TimeSpan.FromHours(3);

    private static DateTimeOffset After => Started + TimeSpan.FromHours(1);

    private static void AssertKept(CleanupPlan plan, string path)
    {
        Assert.DoesNotContain(path, plan.TargetedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(plan.ProtectedPaths, p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> Offered(CleanupPlan plan) =>
        plan.Steps.OfType<DeleteDirectoryStep>().Select(s => s.Path);

    [Fact]
    public async Task ReportsNotPresentWhereClaudeCodeHasTakenNoSnapshots()
    {
        Write(Path.Combine(_environment.TempPath, "claude", ProjectFolder, SessionA, "tasks"), "a.output", 128);

        Assert.False(await CreateProvider().IsPresentAsync());
    }

    /// <summary>
    /// A snapshot made before the running session's process started goes, one made after stays, and Claude
    /// Code's folder and anything in it the row does not recognise survive (§5.2, §5.6).
    /// </summary>
    [Fact]
    public async Task TakesWhatWasMadeBeforeEveryRunningSessionAndNothingBesideIt()
    {
        SessionStarted();
        var ended = Snapshot(Before);
        var running = Snapshot(After);
        var unrecognised = Write(Path.Combine(Snapshots, "notes"), "keep.txt", 512);

        var provider = CreateProvider();
        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();

        Assert.Equal(SafetyTier.RegenerableCache, plan.Tier);
        Assert.Equal([ended], Offered(plan));
        AssertKept(plan, running);
        AssertKept(plan, Snapshots);
        AssertKept(plan, Path.GetDirectoryName(unrecognised)!);

        var result = await provider.ExecuteAsync(plan);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(ended), "a snapshot no running session can have made survived");
        Assert.True(Directory.Exists(running), "a snapshot a running session may be using was removed");
        Assert.True(File.Exists(unrecognised), "something in the snapshot folder Deguffer does not recognise was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The trap the design is built around. A process keeps its snapshot when it moves to another session,
    /// so the list names a session the snapshot's name does not. The snapshot is kept on its time alone.
    /// </summary>
    [Fact]
    public async Task KeepsASnapshotWhoseProcessHasMovedToAnotherSession()
    {
        SessionStarted(SessionB);
        var kept = Snapshot(After);

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        AssertKept(plan, kept);
    }

    /// <summary>
    /// A clock can be stepped back, and a FAT drive's times move with summer time, so a creation time can
    /// read earlier than it was. Made only a little before the session started is not made before it.
    /// </summary>
    [Fact]
    public async Task KeepsASnapshotMadeJustBeforeTheSessionStarted()
    {
        SessionStarted();
        var close = Snapshot(Started - TimeSpan.FromMinutes(90));

        var plan = await CreateProvider().PlanAsync();

        Assert.Empty(plan.Steps);
        AssertKept(plan, close);
    }

    /// <summary>
    /// A snapshot whose removal Claude Code began has an index and no <c>HEAD</c>. It goes even while the
    /// process that made it runs, because a process open for days collects these, and the clean has
    /// nothing to ask again about it.
    /// </summary>
    [Fact]
    public async Task TakesASnapshotClaudeCodeHasBegunToRemoveWhateverMadeIt()
    {
        SessionStarted();
        var inUse = Snapshot(After);
        var abandoned = Snapshot(After, removalBegun: true);
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();

        Assert.Equal([abandoned], Offered(plan));
        AssertKept(plan, inUse);

        var result = await provider.ExecuteAsync(plan);

        Assert.False(Directory.Exists(abandoned), "the clean held back a snapshot Claude Code had begun to remove");
        Assert.True(Directory.Exists(inUse), "a snapshot a running session may be using was removed");
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>A running session whose start nobody could read may be the earliest, so nothing is offered on time.</summary>
    [Fact]
    public async Task OffersNothingOnTimeWhereARunningSessionsStartIsUnknown()
    {
        _claude.Registered(SessionProcess, SessionA);
        var old = Snapshot(Before);

        var plan = await CreateProvider(FakeProcessInspector.NothingRunning.WithProcess(SessionProcess, ProcessLiveness.Undetermined))
            .PlanAsync();

        Assert.Empty(plan.Steps);
        AssertKept(plan, old);
    }

    /// <summary>
    /// A list that cannot be read, and a list Claude Code does not keep here at all, are not lists naming
    /// nobody. Only what needs no list is offered, and the row says why the rest is not.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OffersOnlyBegunRemovalsWithoutAListOfSessions(bool listUnreadable)
    {
        if (listUnreadable)
        {
            Write(_claude.Sessions, $"{SessionProcess}.json", 16);
        }

        var old = Snapshot(Before);
        var abandoned = Snapshot(Before, removalBegun: true);

        var plan = await CreateProvider().PlanAsync();

        Assert.Equal([abandoned], Offered(plan));
        AssertKept(plan, old);
        Assert.Contains(plan.Notes, n =>
            n.Severity == PlanNoteSeverity.Warning && n.Message.Contains("list of running sessions", StringComparison.Ordinal));
    }

    /// <summary>
    /// A preview can sit on screen while a session starts. The clean reads the list again, so a snapshot a
    /// session started since may be using stays, and the run says why.
    /// </summary>
    [Fact]
    public async Task KeepsASnapshotASessionStartedAfterThePreviewMayBeUsing()
    {
        SessionStarted();
        var snapshot = Snapshot(Before);
        var provider = CreateProvider(SessionRunning.WithProcess(SessionProcess + 1, ProcessLiveness.Running(Before - TimeSpan.FromHours(1))));

        var plan = await provider.PlanAsync();
        Assert.Equal([snapshot], Offered(plan));

        _claude.Registered(SessionProcess + 1, SessionB, Before - TimeSpan.FromHours(1));
        var result = await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(snapshot), "the clean removed a snapshot a session running by then may be using");
        Assert.Equal(0, result.BytesReclaimed);
    }

    /// <summary>
    /// A list gone by the clean is not a list naming nobody there either. What the preview offered on
    /// time is held back, as the preview would have held it.
    /// </summary>
    [Fact]
    public async Task KeepsWhatItOfferedOnTimeWhereTheListIsGoneByTheClean()
    {
        SessionStarted();
        var snapshot = Snapshot(Before);
        var provider = CreateProvider();

        var plan = await provider.PlanAsync();
        Assert.Equal([snapshot], Offered(plan));

        Directory.Delete(_claude.Sessions, recursive: true);
        await provider.ExecuteAsync(plan);

        Assert.True(Directory.Exists(snapshot), "the clean took a snapshot on a list Claude Code no longer keeps");
    }

    /// <summary>
    /// The row speaks for the snapshot folder alone. The rest of Claude Code's temporary folder, each
    /// session's scratch and command output, stays the "Temporary files" row's to age out.
    /// </summary>
    [Fact]
    public async Task ClaimsOnlyTheSnapshotFolderFromTheTemporaryFolder()
    {
        Snapshot(Before);
        Write(Path.Combine(_environment.TempPath, "claude", ProjectFolder, SessionA, "scratchpad"), "notes.md", 128);

        var claimed = await CreateProvider().ClaimedEntriesAsync([_environment.TempPath]);

        Assert.Equal([Snapshots], claimed);
    }

    /// <summary>A link where the snapshot folder should be is declined: what is on the far side was never classified.</summary>
    [Fact]
    public async Task NeverFollowsALinkInPlaceOfTheSnapshotFolder()
    {
        var outside = _temp.CreateDirectory("outside");
        var kept = Write(Path.Combine(outside, $"{Hashes}-{0:x16}"), "index", 4096);
        Directory.CreateDirectory(Path.GetDirectoryName(Snapshots)!);
        SymbolicLink.ToDirectory(Snapshots, outside);

        var provider = CreateProvider();
        var plan = await provider.PlanAsync();

        Assert.Empty(plan.Steps);

        await provider.ExecuteAsync(plan);

        Assert.True(File.Exists(kept));
    }
}
