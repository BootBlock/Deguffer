using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The in-use check compares every directory a row asks about with every running program, and a row
/// asks about hundreds of directories against hundreds of processes. Each program's places and each
/// directory's are followed once, so the cost of a pair is a comparison, not a lookup or an allocation.
/// </summary>
public sealed class LiveTreePairCostTests
{
    private const int Candidates = 200;
    private const int Processes = 200;

    /// <summary>
    /// What one candidate or one program may allocate on a warm call: its reached form, its holder
    /// list and the small arrays beside them. Even one 24-byte object per pair is 960,000 bytes over
    /// the 40,000 pairs, more than twice this bound, and the per-pair loop this replaced allocated
    /// about 28 MB here.
    /// </summary>
    private const long BytesPerCandidateOrProcess = 1024;

    private static readonly DateTimeOffset Written = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// After a warm-up call has read the table and followed every place, a second call over 200
    /// candidates and 200 programs allocates in proportion to the candidates and programs, not to the
    /// pairs. The candidate the last program in the table is working in is still found, so the bound
    /// was not met by comparing fewer pairs (§5.6).
    /// </summary>
    [Fact]
    public void FindingLiveTreesAllocatesPerCandidateAndProgramNotPerPair()
    {
        var queries = Queries();
        var inspector = Inspector(queries[^1].Project);

        var warm = inspector.FindLive(queries);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var findings = inspector.FindLive(queries);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(warm.Live.Select(tree => tree.Directory), findings.Live.Select(tree => tree.Directory));
        Assert.True(findings.Complete);
        var live = Assert.Single(findings.Live);
        Assert.Equal(queries[^1].Directory, live.Directory);
        Assert.Equal(["pwsh is working in project199"], live.Holders);
        Assert.True(
            allocated < (Candidates + Processes) * BytesPerCandidateOrProcess,
            $"FindLive allocated {allocated:N0} bytes for {Candidates} candidates against {Processes} programs.");
    }

    /// <summary>
    /// The programs followed for one reading are kept for that reading alone. Asked about a newer
    /// table, the rule follows its programs rather than answering with the old reading's, which would
    /// miss a program started since and offer the directory it is working in (§5.6).
    /// </summary>
    [Fact]
    public void ProgramsFollowedForOneReadingAreNotReusedForAnother()
    {
        var match = new LiveTreeMatch(new FakeVolumeInventory());
        var earlier = Table(new RunningProcess(5000, "tool", @"C:\Program Files\Tool\tool.exe", null, []));
        var later = Table(new RunningProcess(5001, "pwsh", null, @"C:\Users\testuser\src\app\", []));

        Assert.Equal("tool", Assert.Single(match.Programs(earlier)).Name);
        Assert.Equal("pwsh", Assert.Single(match.Programs(later)).Name);
    }

    private static ProcessTable Table(RunningProcess process) => new([process], true, true, true);

    /// <summary>
    /// Every candidate names a project, a workspace and writer folders, so every rule is asked of every
    /// pair, and none of them matches but the last program's.
    /// </summary>
    private static List<LiveTreeQuery> Queries() =>
    [
        .. Enumerable.Range(0, Candidates).Select(i =>
        {
            var project = $@"C:\Users\testuser\src\project{i}";

            return new LiveTreeQuery(project + @"\obj", project)
            {
                Workspaces = [$@"C:\Users\testuser\src\solution{i}"],
                Writers = new LiveTreeWriters([$@"C:\Users\testuser\AppData\Local\Updater{i}"], Written),
            };
        }),
    ];

    /// <summary>
    /// <see cref="Processes"/> programs, each running from its own folder, working in its own folder
    /// and started before every candidate was written, then a shell working in
    /// <paramref name="heldProject"/> last.
    /// </summary>
    private static LiveTreeInspector Inspector(string heldProject)
    {
        var calls = new FakeProcessTableCalls().With(FakeProcessTableCalls.Own);

        for (var i = 0; i < Processes - 1; i++)
        {
            calls.With(new FakeListedProcess
            {
                Id = 5000 + i,
                Name = $"tool{i}",
                ImagePath = $@"C:\Program Files\Tool{i}\tool.exe",
                CommandLine = "tool.exe",
                StartTime = Written.AddHours(-1),
                Memory = FakeProcessMemory.Native($@"C:\Users\testuser\work{i}\", "tool.exe"),
            });
        }

        calls.With(new FakeListedProcess
        {
            Id = 5000 + Processes,
            Name = "pwsh",
            ImagePath = @"C:\Program Files\PowerShell\7\pwsh.exe",
            CommandLine = "pwsh.exe",
            StartTime = Written.AddHours(-1),
            Memory = FakeProcessMemory.Native(heldProject + @"\", "pwsh.exe"),
        });

        return new LiveTreeInspector(calls, new FakeVolumeInventory());
    }
}
