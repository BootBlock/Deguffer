using Deguffer.App.Shell;
using Deguffer.Core.Diagnostics;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How a fault in work queued on the dispatcher reaches <c>crash.log</c>. The callback is run directly
/// rather than through a real queue, because a fault that escapes one ends the process, and the test
/// host with it.
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
}
