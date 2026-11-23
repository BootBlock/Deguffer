using System.Collections.Concurrent;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How the process table is read: several processes at once, into the table one at a time would
/// give, and as one reading every asker waits for outside the inspector's lock.
/// </summary>
public sealed class ProcessTableReadingTests : IDisposable
{
    private const string Project = @"C:\Users\testuser\src\app";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // Released by every test before it ends, so an open held in flight never outlives it.
    private readonly ManualResetEventSlim _release = new();
    private readonly ManualResetEventSlim _opening = new();

    public void Dispose()
    {
        _release.Set();
        _release.Dispose();
        _opening.Dispose();
    }

    /// <summary>
    /// Two opens, each held until the other has begun, both begin. Read one after another, the first
    /// waits for the second, which has not started, and gives up.
    /// </summary>
    [Fact]
    public void TwoProcessesAreOpenedAtOnce()
    {
        using var both = new CountdownEvent(2);
        var met = new ConcurrentBag<bool>();

        void Meet()
        {
            both.Signal();
            met.Add(both.Wait(Patience));
        }

        var calls = new FakeProcessTableCalls()
            .With(FakeProcessTableCalls.Own)
            .With(new FakeListedProcess { Id = 4400, WhileOpening = Meet })
            .With(new FakeListedProcess { Id = 4401, WhileOpening = Meet });

        RunningProcessTable.Read(calls, threads: 2, CancellationToken.None);

        Assert.Equal([true, true], met);
    }

    /// <summary>
    /// Read several at once, the table lists the processes in the order the system did, leaves out
    /// the one that refused to open, and keeps the doubt one process's directory raised, exactly as a
    /// read of one at a time does. Read several at once, the first process is held until the last
    /// has been opened, so it finishes last, and a table gathered as the reads finished would list
    /// it last.
    /// </summary>
    [Fact]
    public void TheTableReadSeveralAtOnceIsTheTableReadOneAtATime()
    {
        var calls = new FakeProcessTableCalls().With(FakeProcessTableCalls.Own);
        using var lastOpened = new ManualResetEventSlim();
        var holdFirst = false;

        for (var id = 5000; id < 5040; id++)
        {
            calls.With(new FakeListedProcess
            {
                Id = id,
                Name = $"p{id}",
                OpenRefused = id % 7 == 0,
                ImagePath = $@"C:\Tools\p{id}\p{id}.exe",
                CommandLine = $@"p{id}.exe C:\Users\testuser\src\p{id}",
                StartTime = DateTimeOffset.UnixEpoch.AddSeconds(id),
                WhileOpening = id switch
                {
                    5001 => () => Assert.True(!holdFirst || lastOpened.Wait(Patience)),
                    5039 => lastOpened.Set,
                    _ => null,
                },
                Memory = id == 5013
                    ? FakeProcessMemory.Wow64(Project + @"\", @"C:\Windows\", "cmd.exe", wow64CommandLine: "garbage")
                    : FakeProcessMemory.Native($@"C:\Users\testuser\src\p{id}\", $"p{id}.exe"),
            });
        }

        var one = RunningProcessTable.Read(calls, threads: 1, CancellationToken.None);
        lastOpened.Reset();
        holdFirst = true;
        var many = RunningProcessTable.Read(calls, threads: 8, CancellationToken.None);

        Assert.Equal(Describe(one), Describe(many));
        Assert.Equal(
            [Environment.ProcessId, .. Enumerable.Range(5000, 40).Where(id => id % 7 != 0)],
            many.Processes.Select(p => p.Id));
        Assert.False(many.CurrentDirectoriesReadable);
        Assert.True(many.ImagePathsReadable);
        Assert.True(many.CommandLinesReadable);
    }

    /// <summary>
    /// While the table is being read, another thread drops the reading and follows a path without
    /// waiting for the read. Read under the lock, both waited until it ended.
    /// </summary>
    [Fact]
    public async Task AReadUnderWayHoldsNoLock()
    {
        var (calls, inspector) = HeldRead();
        var asking = Task.Run(() => inspector.FindOccupiedDirectoriesAsync());
        Assert.True(_opening.Wait(Patience));

        var others = Task.Run(() =>
        {
            inspector.Reach(Project);
            inspector.DiscardOlderThan(TimeSpan.Zero);
            inspector.Invalidate();
        });

        await others.WaitAsync(Patience);
        Assert.False(asking.IsCompleted);

        _release.Set();
        await asking.WaitAsync(Patience);
        Assert.Equal(1, calls.ListCount);
    }

    /// <summary>
    /// An asker that arrives while the table is being read is handed the same reading, awaited rather
    /// than waited for, and the table is listed once.
    /// </summary>
    [Fact]
    public async Task EveryAskerDuringTheReadAwaitsTheSameReading()
    {
        var (calls, inspector) = HeldRead();
        var first = inspector.ReadingAsync();
        Assert.True(_opening.Wait(Patience));

        var second = inspector.ReadingAsync();
        Assert.False(second.IsCompleted);

        _release.Set();

        Assert.Equal(await first.WaitAsync(Patience), await second.WaitAsync(Patience));
        Assert.Equal(1, calls.ListCount);
    }

    /// <summary>
    /// The asker that started the read is stopped, which stops the read. Another asker who was not
    /// stopped is read for again, rather than handed the stop.
    /// </summary>
    [Fact]
    public async Task AReadStoppedForItsStarterIsReadAgainForAnotherAsker()
    {
        var (calls, inspector) = HeldRead();
        using var stop = new CancellationTokenSource();
        var starter = inspector.ReadingAsync(stop.Token);
        Assert.True(_opening.Wait(Patience));

        var other = inspector.ReadingAsync();
        await stop.CancelAsync();
        _release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starter.WaitAsync(Patience));
        await other.WaitAsync(Patience);
        Assert.Equal(2, calls.ListCount);
    }

    /// <summary>A read that failed is not kept: the next question reads again.</summary>
    [Fact]
    public async Task AFailedReadIsNotKept()
    {
        var calls = new FakeProcessTableCalls().With(FakeProcessTableCalls.Own);
        calls.ListFailure = new InvalidOperationException("The list could not be read.");
        var inspector = new LiveTreeInspector(calls, new FakeVolumeInventory());

        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.FindOccupiedDirectoriesAsync());

        Assert.True((await inspector.FindOccupiedDirectoriesAsync()).Complete);
        Assert.Equal(1, calls.ListCount);
    }

    /// <summary>An inspector whose reads hold one process's open until the test releases it.</summary>
    private (FakeProcessTableCalls Calls, LiveTreeInspector Inspector) HeldRead()
    {
        var calls = new FakeProcessTableCalls()
            .With(FakeProcessTableCalls.Own)
            .With(new FakeListedProcess
            {
                Id = 4600,
                Name = "build",
                WhileOpening = () =>
                {
                    _opening.Set();
                    // Longer than any test waits, so a test whose asker is held behind the read times
                    // out while the read is still held, rather than after it gave up.
                    _release.Wait(Patience * 3);
                },
            })
            .With(new FakeListedProcess { Id = 4601, Name = "shell" });

        return (calls, new LiveTreeInspector(calls, new FakeVolumeInventory()));
    }

    private static IEnumerable<string> Describe(ProcessTable table) =>
    [
        $"{table.ImagePathsReadable} {table.CurrentDirectoriesReadable} {table.CommandLinesReadable}",
        .. table.Processes.Select(p =>
            $"{p.Id} {p.Name} {p.ImagePath} {p.CurrentDirectory} {string.Join('|', p.PathArguments)} {p.Started}"),
    ];
}
