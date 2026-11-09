using System.Runtime.CompilerServices;
using Deguffer.App.Shell;
using Deguffer.Core.Diagnostics;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How a fault in work queued on the dispatcher reaches <c>crash.log</c>, and how a timer's tick holds
/// what it runs on. A faulting callback is run directly rather than through a real queue, because a
/// fault that escapes one ends the process, and the test host with it.
/// </summary>
public sealed class DispatcherFaultsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly CrashLog _faults;

    public DispatcherFaultsTests()
    {
        _faults = new CrashLog(new FakeUserEnvironment(_temp.Path));
    }

    public void Dispose() => _temp.Dispose();

    private string Logged() => File.Exists(_faults.FilePath) ? File.ReadAllText(_faults.FilePath) : string.Empty;

    [Fact]
    public void A_fault_is_recorded_under_its_source_and_still_escapes()
    {
        var fault = new InvalidOperationException("The layout failed.");

        var escaped = Assert.Throws<InvalidOperationException>(
            () => DispatcherFaults.Run(_faults, DispatcherFaults.Ticked, () => throw fault));

        Assert.Same(fault, escaped);
        Assert.Contains(DispatcherFaults.Ticked, Logged());
        Assert.Contains("The layout failed.", Logged());
    }

    [Fact]
    public void A_fault_is_recorded_before_the_callback_unwinds()
    {
        // A record taken while unwinding, or after, would see the state the callback's own clean-up
        // left rather than the state it faulted in, and the frame that threw would be gone from a dump.
        var recordedBeforeUnwinding = false;

        Assert.Throws<InvalidOperationException>(() => DispatcherFaults.Run(_faults, DispatcherFaults.Enqueued, () =>
        {
            try
            {
                throw new InvalidOperationException("The paint failed.");
            }
            finally
            {
                recordedBeforeUnwinding = Logged().Contains("The paint failed.", StringComparison.Ordinal);
            }
        }));

        Assert.True(recordedBeforeUnwinding);
    }

    [Fact]
    public void A_callback_that_returns_records_nothing()
    {
        var ran = false;

        DispatcherFaults.Run(_faults, DispatcherFaults.Enqueued, () => ran = true);

        Assert.True(ran);
        Assert.False(File.Exists(_faults.FilePath));
    }

    [Fact]
    public void ATimersOwnerIsCollectedWhileTheTimerLives()
    {
        // The timer is a native object that holds its handler as a root the collector cannot see past,
        // so a handler that reached the owner kept it for the life of the process (#737).
        var (timer, owner) = CompositionThread.Run(GuardedTimer);

        Assert.True(Collector.Reclaims(owner));
        GC.KeepAlive(timer);
    }

    [Fact]
    public async Task ATickThatFindsItsOwnerGoneStopsTheTimer()
    {
        var (timer, owner) = CompositionThread.Run(GuardedTimer);

        Assert.True(Collector.Reclaims(owner));

        CompositionThread.Run(() =>
        {
            timer.Start();
            return true;
        });

        await Eventually.HoldsAsync(() => !CompositionThread.Run(() => timer.IsRunning), "the timer stopped");
    }

    [Fact]
    public void ACallbackThatCapturesIsRefused()
    {
        var ticks = 0;

        var refused = CompositionThread.Run(() => Record.Exception(() =>
            CompositionThread.Queue.CreateTimer().TickGuarded(new Owner(), _ => ticks++)));

        Assert.IsType<ArgumentException>(refused);
    }

    /// <summary>
    /// A repeating timer ticking on an owner nothing else holds. Not inlined, so nothing on the test's
    /// own stack holds the owner.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Microsoft.UI.Dispatching.DispatcherQueueTimer Timer, WeakReference Owner) GuardedTimer()
    {
        var timer = CompositionThread.Queue.CreateTimer();
        var owner = new Owner();

        timer.Interval = TimeSpan.FromMilliseconds(1);
        timer.IsRepeating = true;
        timer.TickGuarded(owner, static owner => owner.Ticks++);

        return (timer, new WeakReference(owner));
    }

    private sealed class Owner
    {
        public int Ticks;
    }
}
