using System.Collections.Concurrent;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// Many rows build their declarations from a walk or a survey, and read one after another those ran
/// in series. These prove that the rows are read together, no more of them at
/// once than a pass plans, each on a thread of the walk's own, and that what they declare comes back
/// in the rows' order however the reads finish.
/// </summary>
public sealed class DeclaredRootsTests
{
    // Long enough that a read which should start never looks late, and short enough that a serial
    // read fails the test rather than holding it.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static ToolRoot Root(string name) =>
        ToolRoot.Folders(Path.Combine(@"C:\Users\testuser\AppData\Local", name), "A tool's own folder.", _ => false);

    /// <summary>
    /// Each row's declaration waits for every other row's to have started. Read in series, the first
    /// waits for the rest in vain.
    /// </summary>
    [Fact]
    public async Task TheRowsAreReadTogether()
    {
        const int rows = 4;
        using var started = new CountdownEvent(rows);
        var metTheOthers = new ConcurrentQueue<bool>();

        await DeclaredRoots.ReadAsync(
            [
                .. Enumerable.Range(0, rows).Select(_ => new DeclaringRow(() =>
                {
                    started.Signal();
                    metTheOthers.Enqueue(started.Wait(Patience));
                    return [];
                })),
            ],
            default);

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

        await DeclaredRoots.ReadAsync(
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
            ],
            default);

        Assert.Equal(atOnce, most);
    }

    /// <summary>The first row finishes last, and its roots still come first.</summary>
    [Fact]
    public async Task TheRootsComeBackInTheRowsOrder()
    {
        using var secondRead = new ManualResetEventSlim();

        var roots = await DeclaredRoots.ReadAsync(
            [
                new DeclaringRow(() =>
                {
                    secondRead.Wait(Patience);
                    return [Root("first")];
                }),
                new DeclaringRow(() =>
                {
                    secondRead.Set();
                    return [Root("second"), Root("third")];
                }),
            ],
            default);

        Assert.Equal(["first", "second", "third"], roots.Select(root => Path.GetFileName(root.Path)));
    }

    /// <summary>
    /// A declaration may walk, so none is read on a pool thread, and each knows it is being read so
    /// that it cannot wait for the reading it is part of. The caller is not told so once it ends.
    /// </summary>
    [Fact]
    public async Task EachRowIsReadOffThePoolAndKnowsItIsBeingRead()
    {
        var seen = new ConcurrentQueue<(bool OnThePool, bool BeingRead)>();

        await DeclaredRoots.ReadAsync(
            [
                .. Enumerable.Range(0, 3).Select(_ => new DeclaringRow(() =>
                {
                    seen.Enqueue((Thread.CurrentThread.IsThreadPoolThread, DeclaredRoots.IsBeingRead));
                    return [];
                })),
            ],
            default);

        Assert.Equal(3, seen.Count);
        Assert.All(seen, read => Assert.Equal((false, true), read));
        Assert.False(DeclaredRoots.IsBeingRead);
    }
}
