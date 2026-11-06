using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The in-use check asks the Restart Manager about every candidate's lock files in one session,
/// because each session has a cost of its own, and forty solutions paid it forty times.
/// A shared answer is only ever shared where it names nobody: a holder is always read from a session
/// holding one candidate's files, so the plan never names the wrong project.
/// </summary>
public sealed class LiveTreeSharedSessionTests
{
    private const int Candidates = 40;

    private readonly FakeFileHolders _holders = new();

    /// <summary>Forty candidates whose lock files nobody holds are answered by one session.</summary>
    [Fact]
    public void FortyCandidatesWhoseLockFilesNobodyHoldsCostOneSession()
    {
        var queries = Queries();

        var findings = Inspector().FindLive(queries);

        var session = Assert.Single(_holders.Sessions);
        Assert.Equal(queries.SelectMany(query => query.LockFileNames), session);
        Assert.True(findings.Complete);
        Assert.Empty(findings.Live);
        Assert.All(queries, query => Assert.True(findings.IsAnswered(query.Directory)));
    }

    /// <summary>
    /// One held solution among forty is the only one live, and is named with its holder by a session
    /// that held its own files and no others. The shared session that named the holder is followed by
    /// one session for each solution, and every other solution is answered as free (§5.6).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(Candidates - 1)]
    public void OneHeldCandidateAmongFortyIsTheOnlyOneLiveAndNamedWithItsHolder(int open)
    {
        var queries = Queries();
        _holders.Holding(queries[open].LockFileNames[1], "DevHub");

        var findings = Inspector().FindLive(queries);

        var live = Assert.Single(findings.Live);
        Assert.Equal(queries[open].Directory, live.Directory);
        Assert.Equal(["DevHub has it open"], live.Holders);
        Assert.True(findings.Complete);
        Assert.All(queries, query => Assert.True(findings.IsAnswered(query.Directory)));
        List<IReadOnlyList<string>> shared = [[.. queries.SelectMany(query => query.LockFileNames)]];
        Assert.Equal([.. shared, .. queries.Select(query => query.LockFileNames)], _holders.Sessions);
    }

    /// <summary>
    /// A path that fails to register leaves the shared session unanswered, and only the candidate it
    /// belongs to is left "could not tell". The rest are answered as their own sessions answer them,
    /// a held one included, so one unusual path does not hold back forty solutions.
    /// </summary>
    [Fact]
    public void AnUnansweredSharedSessionLeavesEachCandidateWithItsOwnAnswer()
    {
        var queries = Queries();
        _holders.Refusing(queries[5].LockFileNames[0]).Holding(queries[30].LockFileNames[2], "devenv");

        var findings = Inspector().FindLive(queries);

        Assert.False(findings.Complete);
        Assert.False(findings.IsAnswered(queries[5].Directory));
        Assert.Equal([queries[5].Directory], queries.Where(query => !findings.IsAnswered(query.Directory)).Select(query => query.Directory));
        var live = Assert.Single(findings.Live);
        Assert.Equal(queries[30].Directory, live.Directory);
        Assert.Equal(["devenv has it open"], live.Holders);
    }

    /// <summary>
    /// A candidate with no lock files is never asked about, and a single candidate with lock files is
    /// asked in its own session alone, as before: nothing is asked twice to share one session.
    /// </summary>
    [Fact]
    public void OneCandidateWithLockFilesIsAskedInOneSessionOfItsOwn()
    {
        var queries = Queries();
        LiveTreeQuery[] asked = [new(@"C:\Users\testuser\src\app\bin", @"C:\Users\testuser\src\app"), queries[3]];
        _holders.Holding(queries[3].LockFileNames[0], "DevHub");

        var findings = Inspector().FindLive(asked);

        Assert.Equal(queries[3].LockFileNames, Assert.Single(_holders.Sessions));
        Assert.Equal(queries[3].Directory, Assert.Single(findings.Live).Directory);
    }

    private LiveTreeInspector Inspector() =>
        new(new FakeProcessTableCalls().With(FakeProcessTableCalls.Own), new FakeVolumeInventory(), holdersOf: _holders.Query);

    /// <summary>
    /// Forty <c>.vs</c> folders, each with the cache files a listing found in it, which is how the
    /// Visual Studio provider asks.
    /// </summary>
    private static List<LiveTreeQuery> Queries() =>
    [
        .. Enumerable.Range(0, Candidates).Select(i =>
        {
            var solution = $@"C:\Users\testuser\src\solution{i}";
            var vs = Path.Combine(solution, ".vs");

            return new LiveTreeQuery(
                vs,
                solution,
                [.. new[] { "index.vsidx", "Browse.VC.db", "Solution.VC.db" }.Select(name => Path.Combine(vs, name))])
            {
                LockFilesListed = true,
            };
        }),
    ];
}
