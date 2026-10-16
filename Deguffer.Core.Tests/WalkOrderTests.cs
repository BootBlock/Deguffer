using System.Collections.Concurrent;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The walk's order. A walk that took directories from one first-in, first-out queue went
/// breadth-first, and held each level of the tree waiting at once: on a whole system volume its peak
/// working set was about 50 MiB above the depth-first walk's.
/// </summary>
public sealed class WalkOrderTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// One worker reads a branch to its end before the folder beside it, and the first folder listed
    /// first, so Explore's mid-walk picture fills from the first name onward. Breadth-first would read
    /// both top folders before either one's children, and a stack taking the last child pushed would
    /// read them in reverse.
    /// </summary>
    [Fact]
    public void OneWorkerReadsEachBranchToItsEndBeforeTheNext()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(8, "cache", "a", "a1", "leaf.bin");
        _temp.CreateDirectory("cache", "a", "a2");
        _temp.CreateDirectory("cache", "b", "b1");

        var read = new List<string>();

        Walk(root, new WalkTuning(1, WalkTuning.MinimumListingBuffer), (name, _) => read.Add(name));

        Assert.Equal(["cache", "a", "a1", "a2", "b", "b1"], read);
    }

    /// <summary>
    /// A worker with nothing of its own takes the shallowest folder waiting, which holds the most
    /// below it. Here the first worker is held in a folder two levels down, with a folder from each
    /// level waiting above it, until the second worker has read something. The second worker must
    /// take the last top-level folder, not the folder beside the one being read (a shared stack) nor
    /// the next top-level one (a shared queue).
    ///
    /// <para>The second worker is held in turn, in the first folder it took, until the first is held
    /// in its own. Otherwise a first worker slow to be scheduled, as on a busy machine of few
    /// processors, lets the second steal every top-level folder in turn, <c>a</c> among them, and the
    /// first never reaches the folder it is meant to be held in. The second worker's choice is made
    /// when it steals, before it is held, so holding it decides nothing this test asks.</para>
    /// </summary>
    [Fact]
    public void AWorkerThatHasRunOutTakesTheShallowestFolderWaiting()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateDirectory("cache", "a", "a1");
        _temp.CreateDirectory("cache", "a", "a2");
        _temp.CreateDirectory("cache", "b");
        _temp.CreateDirectory("cache", "c");

        var callingThread = Environment.CurrentManagedThreadId;
        var helperReads = new ConcurrentQueue<string>();
        using var helperRead = new ManualResetEventSlim();
        using var heldInA1 = new ManualResetEventSlim();
        var heldUntilHelperRead = false;
        var firstReachedA1 = true;

        Walk(root, new WalkTuning(2, WalkTuning.MinimumListingBuffer), (name, thread) =>
        {
            if (thread != callingThread)
            {
                if (helperReads.IsEmpty)
                {
                    firstReachedA1 = heldInA1.Wait(TimeSpan.FromSeconds(10));
                }

                helperReads.Enqueue(name);
                helperRead.Set();
            }
            else if (name == "a1")
            {
                heldInA1.Set();
                heldUntilHelperRead = helperRead.Wait(TimeSpan.FromSeconds(10));
            }
        });

        Assert.True(firstReachedA1, "The first worker never reached the folder it is held in.");
        Assert.True(heldUntilHelperRead, "The second worker read nothing while the first was held.");
        Assert.True(helperReads.TryPeek(out var first));
        Assert.Equal("c", first);
    }

    /// <summary>Walk <paramref name="root"/>, telling <paramref name="onRead"/> each folder's name and the thread that read it.</summary>
    private static void Walk(string root, WalkTuning tuning, Action<string, int> onRead) =>
        BoundedFileWalk.Visit(
            root,
            "cache",
            tuning,
            FileNames.All,
            (name, contents, descend) =>
            {
                onRead(name, Environment.CurrentManagedThreadId);

                foreach (var entry in contents.Entries.Where(e => e.IsDirectory))
                {
                    descend(entry, entry.Name);
                }
            },
            static () => { },
            TimeProvider.System,
            default);
}
