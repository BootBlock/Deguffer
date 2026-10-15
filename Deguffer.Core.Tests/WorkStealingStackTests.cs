using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// The two ends of a worker's stack, which decide the walk's order: the owner takes the newest, so it
/// descends, and a thief takes the oldest, the shallowest directory waiting, so it leaves the owner's
/// branch alone and moves the most work in one steal.
/// </summary>
public sealed class WorkStealingStackTests
{
    [Fact]
    public void TheOwnerTakesTheNewestPushAndTheFirstOfItFirst()
    {
        var stack = new WorkStealingStack<string>();
        stack.PushAll(["a", "b"]);
        stack.PushAll(["a/1", "a/2"]);

        Assert.Equal(["a/1", "a/2", "a", "b"], PopAll(stack));
    }

    [Fact]
    public void AThiefTakesTheOldestFromTheOtherEnd()
    {
        var stack = new WorkStealingStack<string>();
        stack.PushAll(["a", "b"]);
        stack.PushAll(["a/1", "a/2"]);

        Assert.True(stack.TrySteal(out var stolen));
        Assert.Equal("b", stolen);
        Assert.Equal(["a/1", "a/2", "a"], PopAll(stack));
        Assert.False(stack.TrySteal(out _));
    }

    /// <summary>
    /// Steals move the bottom round the ring, so later pushes wrap past the end of the array, and a
    /// push that then outgrows it has to copy across that wrap and keep the order. Sixteen is the
    /// ring's first size: the bottom is left at slot 10, the next ten pushes fill slots 12 to 15 and
    /// 0 to 5, and the last push grows it.
    /// </summary>
    [Fact]
    public void KeepsItsOrderWhenItGrowsAfterTheBottomHasWrapped()
    {
        var stack = new WorkStealingStack<int>();
        stack.PushAll([.. Enumerable.Range(0, 12)]);

        for (var i = 11; i >= 2; i--)
        {
            Assert.True(stack.TrySteal(out var stolen));
            Assert.Equal(i, stolen);
        }

        stack.PushAll([.. Enumerable.Range(100, 10)]);
        stack.PushAll([.. Enumerable.Range(200, 10)]);

        Assert.Equal([.. Enumerable.Range(200, 10), .. Enumerable.Range(100, 10), 0, 1], PopAll(stack));
    }

    /// <summary>
    /// A taken path is not kept alive by the stack. The point of walking depth-first is that a path
    /// dies soon after it is used, and one left in the array would live as long as the walk.
    ///
    /// <para>A stack each, so that nothing pushed later overwrites the slot a taken item was in and
    /// frees it whether or not the take cleared it.</para>
    /// </summary>
    [Fact]
    public void LetsATakenItemBeCollected()
    {
        var popping = new WorkStealingStack<object>();
        var stealing = new WorkStealingStack<object>();
        var popped = PushAndTake(popping, steal: false);
        var stolen = PushAndTake(stealing, steal: true);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(popped.IsAlive, "A popped item is still held by the stack.");
        Assert.False(stolen.IsAlive, "A stolen item is still held by the stack.");
        GC.KeepAlive(popping);
        GC.KeepAlive(stealing);
    }

    /// <summary>
    /// The owner pushing and popping while thieves steal hands every item out exactly once: none
    /// lost to a steal racing a pop for the last one, and none handed out twice across a growth.
    /// </summary>
    [Fact]
    public async Task HandsEveryItemOutOnceWithThievesStealingBesideTheOwner()
    {
        const int Pushes = 20_000;
        var stack = new WorkStealingStack<int>();
        var taken = new ConcurrentBag<int>();
        var ownerDone = 0;

        var thieves = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (Volatile.Read(ref ownerDone) == 0 || !stack.IsEmpty)
            {
                if (stack.TrySteal(out var item))
                {
                    taken.Add(item);
                }
            }
        })).ToArray();

        for (var i = 0; i < Pushes; i++)
        {
            stack.PushAll([2 * i, (2 * i) + 1]);

            if (stack.TryPop(out var item))
            {
                taken.Add(item);
            }
        }

        Volatile.Write(ref ownerDone, 1);
        await Task.WhenAll(thieves).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(Enumerable.Range(0, 2 * Pushes), taken.Order());
    }

    private static List<T> PopAll<T>(WorkStealingStack<T> stack)
    {
        var popped = new List<T>();
        while (stack.TryPop(out var item))
        {
            popped.Add(item);
        }

        return popped;
    }

    // Not inlined, so no local of the caller's frame keeps the item alive past the collection.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PushAndTake(WorkStealingStack<object> stack, bool steal)
    {
        stack.PushAll([new object()]);
        Assert.True(steal ? stack.TrySteal(out var item) : stack.TryPop(out item));
        return new WeakReference(item);
    }
}
