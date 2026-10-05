using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A step whose result is measured on the disk afterwards is credited with nothing when Windows
/// refuses that reading.
///
/// <para>The executor reports what a tool's command, Windows' own Recycle Bin call or a Disk Cleanup
/// handler freed by subtracting a reading afterwards from the figure before. A refused reading
/// measures zero, so the subtraction used to report the whole estimate as reclaimed: a clean
/// reported as complete on no evidence. Each refusal here is applied by the fake that stands for
/// Windows or the tool, so the look before the run succeeds and only the reading after it is
/// refused. See <see cref="DiskReading"/>.</para>
/// </summary>
public sealed class RefusedAfterReadingTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RefusalRecord _refusals;

    private DeniedDirectory? _denied;

    public RefusedAfterReadingTests() =>
        _refusals = RefusalRecord.For(new FakeUserEnvironment(_temp.CreateDirectory("profile")));

    public void Dispose()
    {
        _denied?.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task ACommandWhoseCacheIsRefusedAfterwardsIsCreditedWithNothing()
    {
        _temp.CreateFile(4096, "tool", "cache", "blob");
        var cache = Path.Combine(_temp.Path, "tool", "cache");

        var command = new RunCommandStep("tool", "clean", "Clear the cache with the tool's own command")
        {
            Estimated = new ScanSize(4096, 4096),
            MeasuredPaths = [cache],
        };

        var runner = new FakeProcessRunner().Replying("tool", _ =>
        {
            _denied = DeniedDirectory.WithUnreadableAttributes(cache);
            return new CommandOutcome(0, string.Empty, string.Empty);
        });

        var outcome = await RunAsync(new PlanExecutor(runner, ParallelEnumerationScanner.Default, _refusals), command);

        Assert.True(outcome.Succeeded);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.Contains($"Windows would not let Deguffer read '{cache}' afterwards", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABinRefusedAfterWindowsEmptiedItIsCreditedWithNothing()
    {
        var bin = _temp.CreateDirectory("volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier);
        _temp.CreateFile(4096, "volumes", "D", "$Recycle.Bin", FakeUserEnvironment.SecurityIdentifier, "$RA1B2C3.txt");

        var emptier = new FakeRecycleBinEmptier(_ =>
        {
            _denied = DeniedDirectory.WithUnreadableAttributes(bin);
            return new RecycleBinEmptyOutcome(Emptied: true);
        });

        var step = new EmptyRecycleBinStep(bin, "A bin") { Estimated = new ScanSize(4096, 4096, Entries: 1) };

        var outcome = await RunAsync(
            new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, _refusals, emptier), step);

        Assert.True(outcome.Succeeded);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.Contains($"Windows would not let Deguffer read '{bin}' afterwards", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHandlerWhoseDirectoryIsRefusedAfterwardsIsCreditedWithNothing()
    {
        var volume = _temp.CreateDirectory("volumes", "C");
        _temp.CreateFile(8192, "volumes", "C", "staging", "setup", "payload.bin");
        var staging = Path.Combine(volume, "staging", "setup");

        var handlers = new FakeDiskCleanupHandlers((_, _) =>
        {
            _denied = DeniedDirectory.WithUnreadableAttributes(staging);
            return new DiskCleanupOutcome(Ran: true);
        });

        var step = new DiskCleanupStep(staging, "Installation files")
        {
            Handler = "Temporary Setup Files",
            Volume = volume,
            Estimated = new ScanSize(8192, 8192),
        };

        var outcome = await RunAsync(
            new PlanExecutor(new FakeProcessRunner(), ParallelEnumerationScanner.Default, _refusals, handlers: handlers), step);

        Assert.True(outcome.Succeeded);
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.Contains($"Windows would not let Deguffer read '{staging}' afterwards", outcome.Message, StringComparison.Ordinal);
    }

    private static async Task<StepOutcome> RunAsync(PlanExecutor executor, CleanupStep step)
    {
        var plan = new CleanupPlan
        {
            ProviderId = "test",
            ProviderName = "Test",
            Tier = SafetyTier.RegenerableCache,
            WhatHappensOnNextUse = "Nothing.",
            Steps = [step],
        };

        var result = await executor.ExecuteAsync(plan, runReach: null, residue: null, progress: null, default);

        return Assert.Single(result.Steps);
    }
}
