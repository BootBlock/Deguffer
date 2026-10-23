using System.Diagnostics;
using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// How fresh a clean's use checks read the running programs (<see cref="IUseCheck.ReadingLimit"/>).
///
/// <para>Each step's check used to discard the process table and read it again, so a clean of a
/// thousand build directories opened every process on the machine a thousand times. These tests hold
/// the clean to one reading for every check within the limit, and to a new one past it, so a program
/// started after the preview is still seen.</para>
/// </summary>
public sealed class CleanReadingAgeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _clock = new();
    private readonly FakeProcessTableCalls _calls = new FakeProcessTableCalls().With(FakeProcessTableCalls.Own);

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The cost the limit removes. Every step of the clean has its own check, and every check is
    /// answered from the one reading the first of them took.
    /// </summary>
    [Fact]
    public async Task EveryCheckWithinTheLimitIsAnsweredFromOneReading()
    {
        var inspector = new LiveTreeInspector(_calls, new FakeVolumeInventory(), _clock);
        var folders = Enumerable.Range(1, 5).Select(i => Populate("scratch", $"run-{i}")).ToList();

        var result = await Execute([.. folders.Select(folder => Removal(folder, inspector))]);

        Assert.All(folders, folder => Assert.False(Directory.Exists(folder), $"{folder} was kept, so the checks did not all run"));
        Assert.Equal(1, _calls.ListCount);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The guarantee the limit keeps. The preview read the machine with nothing in the scratch folder,
    /// a program started working in one of its entries, and the clean came later than the limit. The
    /// check reads again, so the entry stays, and the entry nothing took up is still removed.
    /// </summary>
    [Fact]
    public async Task AReadingPastTheLimitIsReadAgainSoAProgramStartedSinceIsSeen()
    {
        var inspector = new LiveTreeInspector(_calls, new FakeVolumeInventory(), _clock);
        var idle = Populate("scratch", "idle");
        var busy = Populate("scratch", "busy");
        var scratch = Path.GetDirectoryName(busy)!;

        // The preview's reading, with nothing working in the folder.
        Assert.Empty(inspector.FindLiveChildren([scratch]).Live);

        _calls.With(new FakeListedProcess
        {
            Id = 4401,
            Name = "build",
            CommandLine = "build.exe",
            Memory = FakeProcessMemory.Native(busy + Path.DirectorySeparatorChar, "build.exe"),
        });
        _clock.Advance(IUseCheck.ReadingLimit + TimeSpan.FromTicks(1));

        var result = await Execute([Removal(idle, inspector), Removal(busy, inspector)]);

        Assert.False(Directory.Exists(idle), "an entry nothing took up was kept");
        Assert.True(Directory.Exists(busy), "an entry a program started working in after the preview was removed");
        Assert.Contains(result.Steps, step => step.Message == "Nothing was removed: build is working in it.");
        Assert.Equal(2, _calls.ListCount);
        Assert.True(result.Verification!.Passed, result.Verification.Summary);
    }

    /// <summary>
    /// The limit is the oldest a reading may be, not a moment before it: one exactly as old answers,
    /// and one a tick older is read again.
    /// </summary>
    [Fact]
    public void AReadingIsKeptUpToTheLimitAndReadAgainPastIt()
    {
        var reading = new TimedReading<object>(_clock);
        var first = reading.Get(() => new object());

        _clock.Advance(IUseCheck.ReadingLimit);
        Assert.False(reading.ForgetOlderThan(IUseCheck.ReadingLimit));
        Assert.Same(first, reading.Get(() => new object()));

        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(reading.ForgetOlderThan(IUseCheck.ReadingLimit));
        Assert.NotSame(first, reading.Get(() => new object()));
    }

    /// <summary>
    /// The name-based checks' inspector keeps the same bound, against the real process list: a program
    /// started after a reading is not seen while the reading is within the limit, and is seen once it
    /// is past it. The program is a copy of <c>ping.exe</c> under a name no other process has, so
    /// nothing else running on the machine can answer for it.
    /// </summary>
    [Fact]
    public void TheProcessInspectorReadsAgainOnlyPastTheLimit()
    {
        var name = $"deguffer-reading-{Guid.NewGuid():N}";
        var program = Path.Combine(_temp.CreateDirectory("bin"), $"{name}.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"), program);

        var inspector = new ProcessInspector(_clock);
        Assert.Empty(inspector.FindRunning([name]));

        using var running = Start(program);
        Assert.True(
            SpinWait.SpinUntil(() => new ProcessInspector().FindRunning([name]).Count > 0, TimeSpan.FromSeconds(20)),
            "the program never appeared in the process list, so the test below would prove nothing");

        inspector.DiscardOlderThan(IUseCheck.ReadingLimit);
        Assert.Empty(inspector.FindRunning([name]));

        _clock.Advance(IUseCheck.ReadingLimit + TimeSpan.FromTicks(1));
        inspector.DiscardOlderThan(IUseCheck.ReadingLimit);
        Assert.Equal([name], inspector.FindRunning([name]));
    }

    private static DeleteDirectoryStep Removal(string folder, ILiveTreeInspector inspector) =>
        new(folder, "A scratch entry") { UseCheck = new LiveChildrenCheck(inspector, planComplete: true) };

    private string Populate(params string[] parts)
    {
        var folder = _temp.CreateDirectory(parts);
        _temp.CreateFile(4096, [.. parts, "data.bin"]);
        return folder;
    }

    private Task<CleanupResult> Execute(IReadOnlyList<CleanupStep> steps) =>
        new PlanExecutor(
                new FakeProcessRunner(),
                ParallelEnumerationScanner.Default,
                RefusalRecord.For(new FakeUserEnvironment(_temp.Path)))
            .ExecuteAsync(
                new CleanupPlan
                {
                    ProviderId = "test",
                    ProviderName = "Test",
                    Tier = SafetyTier.RegenerableCache,
                    WhatHappensOnNextUse = "Nothing.",
                    Steps = steps,
                },
                runReach: null,
                residue: null,
                progress: null,
                ct: CancellationToken.None);

    private static StoppedOnDispose Start(string program)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("120");
        start.ArgumentList.Add("127.0.0.1");

        return new StoppedOnDispose(Process.Start(start)!);
    }

    private sealed class StoppedOnDispose(Process process) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }

                process.WaitForExit(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone. Nothing to stop.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
