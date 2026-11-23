using System.Collections.Concurrent;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How <see cref="RowDeclarations"/> reads the rows a question needs. Many rows build their
/// declarations from a walk or a survey, and read one after another those ran in series. These prove
/// that a question reads only the rows whose scope covers its folder, that those are read together, no
/// more of them at once than a pass plans, each once per pass and on a thread of the walk's own.
/// </summary>
public sealed class RowDeclarationReadingTests
{
    // Long enough that a read which should start never looks late, and short enough that a serial
    // read fails the test rather than holding it.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private const string Tools = @"C:\Users\testuser\AppData\Local";

    private static readonly string Gradle = Path.Combine(@"C:\Users\testuser", ".gradle");

    private static readonly string Claude = Path.Combine(@"C:\Users\testuser", ".claude");

    private static ToolRoot Offering(string folder, string name) =>
        ToolRoot.Folders(folder, "A tool's own folder.", child => child == name);

    private static RowDeclarations Admitted(params ICleanupProvider[] rows)
    {
        var declarations = new RowDeclarations();
        declarations.Admit(rows);
        return declarations;
    }

    /// <summary>
    /// The reported cost. A row scoped to Claude Code's folder is held part-way through its
    /// declaration, as the conversations row is while it dates thousands of folders. A question about
    /// Gradle's home cannot be answered from it, and is answered without waiting for it, both ways.
    /// </summary>
    [Fact]
    public async Task AQuestionDoesNotWaitForARowScopedElsewhere()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var slow = new DeclaringRow(
            () =>
            {
                entered.Set();
                release.Wait(Patience);
                return [Offering(Claude, "cache")];
            },
            scope: ToolRootScope.Under(Claude));
        var gradle = new DeclaringRow(() => [Offering(Gradle, "caches")], scope: ToolRootScope.Under(Gradle));
        var declarations = Admitted(slow, gradle);

        try
        {
            Assert.True(await declarations.OfferedByARowAsync(Path.Combine(Gradle, "caches")).WaitAsync(Patience));
            Assert.False(await declarations.OfferedByARowAsync(Path.Combine(Gradle, "gradle.properties")).WaitAsync(Patience));
            Assert.False(entered.IsSet, "a question about Gradle's home read the row scoped to Claude Code's folder.");
        }
        finally
        {
            release.Set();
        }

        // The slow row still answers for its own folder.
        Assert.True(await declarations.OfferedByARowAsync(Path.Combine(Claude, "cache")).WaitAsync(Patience));
        Assert.True(entered.IsSet);
    }

    /// <summary>
    /// A row is read for a question about any folder inside its scope, not only the scope itself:
    /// the Chromium rows are scoped to the application-data roots and declare folders deep inside them.
    /// </summary>
    [Fact]
    public async Task ARowIsReadForAFolderDeepInsideItsScope()
    {
        var profile = Path.Combine(Tools, "Vendor", "Browser", "User Data", "Default");
        var declarations = Admitted(new DeclaringRow(() => [Offering(profile, "Cache")], scope: ToolRootScope.Under(Tools)));

        Assert.True(await declarations.OfferedByARowAsync(Path.Combine(profile, "Cache")));
    }

    /// <summary>
    /// The scope is compared at a folder boundary and in the resolved form of each path (§6.3). A
    /// scope written with the device prefix covers a folder asked about without it, and a folder
    /// whose name only begins with the scope's is outside it.
    /// </summary>
    [Fact]
    public async Task TheScopeIsComparedAtAFolderBoundaryInTheResolvedForm()
    {
        var reads = 0;
        var toolbox = Gradle + "box";
        var declarations = Admitted(new DeclaringRow(
            () =>
            {
                Interlocked.Increment(ref reads);
                return [Offering(Gradle, "caches")];
            },
            scope: ToolRootScope.Under(@"\\?\" + Gradle + @"\")));

        Assert.False(await declarations.OfferedByARowAsync(Path.Combine(toolbox, "caches")));
        Assert.Equal(0, reads);

        Assert.True(await declarations.OfferedByARowAsync(Path.Combine(Gradle, "caches")));
        Assert.True(await declarations.OfferedByARowAsync(@"\\?\" + Path.Combine(Gradle, "caches")));
        Assert.Equal(1, reads);
    }

    /// <summary>A row that declares no root is never read: the default of every row that owns none.</summary>
    [Fact]
    public async Task ARowScopedNowhereIsNeverRead()
    {
        var declarations = Admitted(new DeclaringRow(
            () => throw new InvalidOperationException("A row that declares no root was read."),
            scope: ToolRootScope.None));

        Assert.False(await declarations.OfferedByARowAsync(Path.Combine(Gradle, "caches")));
    }

    /// <summary>
    /// A row is read once per pass, however many folders inside its scope are asked about, and read
    /// again once the pass is invalidated.
    /// </summary>
    [Fact]
    public async Task ARowIsReadOncePerPassForEveryFolderItsScopeCovers()
    {
        var reads = 0;
        var declarations = Admitted(new DeclaringRow(() =>
        {
            Interlocked.Increment(ref reads);
            return [Offering(Gradle, "caches"), Offering(Claude, "cache")];
        }));

        Assert.True(await declarations.OfferedByARowAsync(Path.Combine(Gradle, "caches")));
        Assert.True(await declarations.OfferedByARowAsync(Path.Combine(Claude, "cache")));
        Assert.Equal(1, reads);

        declarations.Invalidate();

        Assert.True(await declarations.OfferedByARowAsync(Path.Combine(Gradle, "caches")));
        Assert.Equal(2, reads);
    }

    /// <summary>
    /// Each row a question covers waits for every other to have started. Read in series, the first
    /// waits for the rest in vain.
    /// </summary>
    [Fact]
    public async Task TheRowsAQuestionCoversAreReadTogether()
    {
        const int rows = 4;
        using var started = new CountdownEvent(rows);
        var metTheOthers = new ConcurrentQueue<bool>();

        await Admitted(
            [
                .. Enumerable.Range(0, rows).Select(_ => new DeclaringRow(() =>
                {
                    started.Signal();
                    metTheOthers.Enqueue(started.Wait(Patience));
                    return [];
                })),
            ])
            .OfferedByARowAsync(Path.Combine(Gradle, "caches"));

        Assert.Equal(rows, metTheOthers.Count);
        Assert.All(metTheOthers, Assert.True);
    }

    /// <summary>
    /// The first rows are each held until as many as a pass plans have started, and a little after,
    /// so an unbounded read would have more of them in at once.
    /// </summary>
    [Fact]
    public async Task NoMoreRowsAreReadAtOnceThanAPassPlans()
    {
        const int atOnce = CleanupPlanner.ProvidersAtOnce;
        using var full = new ManualResetEventSlim();
        var gate = new Lock();
        var arrived = 0;
        var reading = 0;
        var most = 0;

        await Admitted(
            [
                .. Enumerable.Range(0, atOnce * 3).Select(_ => new DeclaringRow(() =>
                {
                    lock (gate)
                    {
                        most = Math.Max(most, ++reading);

                        if (++arrived >= atOnce)
                        {
                            full.Set();
                        }
                    }

                    full.Wait(Patience);

                    // Each slot held a little longer, the time an unbounded read takes to start the rest.
                    Thread.Sleep(250);

                    lock (gate)
                    {
                        reading--;
                    }

                    return [];
                })),
            ])
            .OfferedByARowAsync(Path.Combine(Gradle, "caches"));

        Assert.Equal(atOnce, most);
    }

    /// <summary>
    /// A declaration may walk, so none is read on a pool thread, and each knows it is being read so
    /// that it cannot wait for the reading it is part of. The caller is not told so once it ends.
    /// </summary>
    [Fact]
    public async Task EachRowIsReadOffThePoolAndKnowsItIsBeingRead()
    {
        var seen = new ConcurrentQueue<(bool OnThePool, bool BeingRead)>();

        await Admitted(
            [
                .. Enumerable.Range(0, 3).Select(_ => new DeclaringRow(() =>
                {
                    seen.Enqueue((Thread.CurrentThread.IsThreadPoolThread, DeclaredRoots.IsBeingRead));
                    return [];
                })),
            ])
            .OfferedByARowAsync(Path.Combine(Gradle, "caches"));

        Assert.Equal(3, seen.Count);
        Assert.All(seen, read => Assert.Equal((false, true), read));
        Assert.False(DeclaredRoots.IsBeingRead);
    }
}
