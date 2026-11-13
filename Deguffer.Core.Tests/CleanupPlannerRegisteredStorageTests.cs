using Deguffer.Core.Execution;
using Deguffer.Core.Execution.Timing;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.VirtualDisks;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A planning pass reads the registered virtual machine storage beside its rows rather than before
/// them (#1014), and still holds every plan to the one set it reads: a row's plan is reported only once
/// the set has been applied to it, and a set that cannot be read fails the pass with no plan reported.
/// </summary>
public sealed class CleanupPlannerRegisteredStorageTests
{
    /// <summary>Far longer than a pass of fakes takes, and far shorter than a hung test.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A row plans while the registered storage is still being read. Read before the first slot, every
    /// row waited for the slowest registrar.
    /// </summary>
    [Fact]
    public async Task ARowPlansBeforeTheRegisteredStorageHasBeenRead()
    {
        var registrar = new GatedRegistrar("registrar");
        using var planning = new ManualResetEventSlim();
        var row = new FakeCleanupProvider("row") { WhilePlanning = planning.Set };

        var pass = new CleanupPlanner([row, registrar], volumes: new FakeVolumeInventory()).PlanAllAsync();

        Assert.True(planning.Wait(Deadline), "No row planned until the registered storage had been read.");
        Assert.False(pass.IsCompleted, "The pass ended before its registered storage was read.");

        registrar.Answer([]);
        await pass.WaitAsync(Deadline);
    }

    /// <summary>
    /// A row whose plan has ended gives its slot up while it waits for the set, so a pass of more rows
    /// than it has slots plans every one of them before the set is read.
    /// </summary>
    [Fact]
    public async Task EveryRowPlansBeforeTheSetIsReadThoughThereAreMoreRowsThanSlots()
    {
        var registrar = new GatedRegistrar("registrar");
        using var planned = new CountdownEvent(CleanupPlanner.ProvidersAtOnce + 1);
        var rows = Enumerable.Range(0, CleanupPlanner.ProvidersAtOnce + 1)
            .Select(index => (ICleanupProvider)new FakeCleanupProvider($"p{index}") { WhilePlanning = () => planned.Signal() })
            .ToList();

        var pass = new CleanupPlanner([.. rows, registrar], volumes: new FakeVolumeInventory()).PlanAllAsync();

        Assert.True(planned.Wait(Deadline), "A row waiting for the registered storage kept its slot from the rows after it.");

        registrar.Answer([]);
        await pass.WaitAsync(Deadline);
    }

    /// <summary>
    /// The registrars are asked together. Asked in turn, the second is not asked until the first
    /// answers, and the first here does not answer until the second has been asked.
    /// </summary>
    [Fact]
    public async Task EveryRegistrarIsAskedBeforeAnyAnswers()
    {
        var first = new GatedRegistrar("first");
        var second = new GatedRegistrar("second");

        var pass = new CleanupPlanner([first, second], volumes: new FakeVolumeInventory()).PlanAllAsync();

        // Each wait fails with a timeout where that registrar is never asked.
        await first.Asked.WaitAsync(Deadline);
        await second.Asked.WaitAsync(Deadline);

        first.Answer([]);
        second.Answer([]);
        await pass.WaitAsync(Deadline);
    }

    /// <summary>
    /// The set is in the registrars' order whatever order they answer in, as it was when they were asked
    /// in turn.
    /// </summary>
    [Fact]
    public async Task TheSetIsInTheRegistrarsOrderWhateverOrderTheyAnswerIn()
    {
        var first = new GatedRegistrar("first");
        var second = new GatedRegistrar("second");
        const string firstDisk = @"C:\Users\testuser\VMs\first.vhdx";
        const string secondDisk = @"C:\Users\testuser\VMs\second.vhdx";

        var reading = RegisteredStorage.ReadAsync([first, second], new FakeVolumeInventory(), CancellationToken.None);

        await second.Asked.WaitAsync(Deadline);
        second.Answer([new VirtualMachineStorage(secondDisk, "the second machine")]);
        first.Answer([new VirtualMachineStorage(firstDisk, "the first machine")]);

        var registered = await reading.WaitAsync(Deadline);

        Assert.Equal([firstDisk, secondDisk], registered.Entries.Select(entry => entry.Storage.Path));
        Assert.Equal(["first", "second"], registered.Entries.Select(entry => entry.RegisteredBy));
    }

    /// <summary>
    /// A row whose plan ended before the set was read is still held to it (§5.2): the step that would
    /// take the registered disk is withheld, and the disk is protected so the clean proves it standing
    /// (§5.6). The row's step beside the disk's folder, which no record names, is left as it was.
    /// </summary>
    [Fact]
    public async Task ARowThatEndedBeforeTheSetWasReadIsHeldToIt()
    {
        using var temp = new TempDirectory();
        var machines = temp.CreateDirectory("machines");
        var disk = Path.Combine(machines, "ubuntu.vhdx");
        File.WriteAllBytes(disk, [1]);
        var beside = temp.CreateDirectory("build-1");

        var registrar = new GatedRegistrar("registrar");
        using var planned = new ManualResetEventSlim();
        var row = new FakeCleanupProvider("row")
        {
            Steps = [new DeleteDirectoryStep(machines, "Machines"), new DeleteDirectoryStep(beside, "Build")],
            WhilePlanning = planned.Set,
        };
        var found = new ProgressRecorder<Finding>();

        var pass = new CleanupPlanner([row, registrar], volumes: new FakeVolumeInventory()).PlanAllAsync(found: found);

        Assert.True(planned.Wait(Deadline), "The row did not plan before the set was read.");
        Assert.DoesNotContain(found.Reports, finding => finding.Provider == row);

        registrar.Answer([new VirtualMachineStorage(disk, "the machine 'Ubuntu'")]);
        var findings = await pass.WaitAsync(Deadline);

        var plan = Assert.Single(findings, finding => finding.Provider == row).Plan!;
        Assert.Equal([beside], plan.TargetedPaths);
        Assert.Contains(plan.ProtectedPaths, p => p.Path == disk && p.Withheld == Withholding.VirtualMachineStorage);
        Assert.Same(plan, Assert.Single(found.Reports, finding => finding.Provider == row).Plan);
    }

    /// <summary>
    /// A registrar whose reading fails fails the pass with its failure, and no row's plan is reported,
    /// because a plan not held to the set could take a registered disk.
    /// </summary>
    [Fact]
    public async Task ARegistrarThatFailsFailsThePassAndNoPlanIsReported()
    {
        var registrar = new GatedRegistrar("registrar");
        using var planned = new ManualResetEventSlim();
        var row = new FakeCleanupProvider("row") { WhilePlanning = planned.Set };
        var found = new ProgressRecorder<Finding>();

        var pass = new CleanupPlanner([row, registrar], volumes: new FakeVolumeInventory()).PlanAllAsync(found: found);

        Assert.True(planned.Wait(Deadline), "The row did not plan before the set was read.");
        registrar.Fail(new IOException("The records could not be read."));

        var failure = await Assert.ThrowsAsync<IOException>(() => pass.WaitAsync(Deadline));
        Assert.Equal("The records could not be read.", failure.Message);
        Assert.DoesNotContain(found.Reports, finding => finding.Plan is not null);
    }

    /// <summary>
    /// A pass of rows that are all absent awaits no set to hold a plan to, and still fails when the set
    /// cannot be read.
    /// </summary>
    [Fact]
    public async Task APassOfAbsentRowsStillFailsWhenTheSetCannotBeRead()
    {
        var registrar = new GatedRegistrar("registrar");
        var absent = new FakeCleanupProvider("absent") { IsPresent = false };

        var pass = new CleanupPlanner([absent, registrar], volumes: new FakeVolumeInventory()).PlanAllAsync();
        registrar.Fail(new IOException("The records could not be read."));

        await Assert.ThrowsAsync<IOException>(() => pass.WaitAsync(Deadline));
    }

    /// <summary>
    /// A registrar whose reading fails stops the rows still planning, as a row whose plan fails does,
    /// rather than leaving them to plan for a pass that can report nothing.
    /// </summary>
    [Fact]
    public async Task ARegistrarThatFailsStopsTheRowsStillPlanning()
    {
        var registrar = new GatedRegistrar("registrar");
        using var started = new ManualResetEventSlim();
        var stopped = false;
        var slow = new FakeCleanupProvider("slow")
        {
            WhilePlanning = started.Set,
            AwaitWhilePlanning = async ct =>
            {
                try
                {
                    await Task.Delay(Deadline * 2, ct);
                }
                catch (OperationCanceledException)
                {
                    // The stop this test is waiting for.
                    stopped = true;
                    throw;
                }
            },
        };

        var pass = new CleanupPlanner([slow, registrar], volumes: new FakeVolumeInventory()).PlanAllAsync();

        Assert.True(started.Wait(Deadline), "The row never started planning.");
        registrar.Fail(new IOException("The records could not be read."));

        await Assert.ThrowsAsync<IOException>(() => pass.WaitAsync(Deadline));
        Assert.True(stopped, "The row went on planning after the set could not be read.");
    }

    /// <summary>
    /// A row's time is its own plan. Its wait for the set is the pass's, and counting it would weigh a
    /// quick row as slow in the next pass's order (<see cref="PlanTimeHistory"/>).
    /// </summary>
    ///
    /// <para>The wait is made after the row's time has been read, deterministically: every other slot is
    /// held, so the row queued behind them starts only once the timed row has given its slot up, and it is
    /// that row which moves the clock on and answers the registrar.</para>
    [Fact]
    public async Task ARowsTimeLeavesOutItsWaitForTheSet()
    {
        var clock = new ManualTimeProvider();
        var registrar = new GatedRegistrar("registrar");
        using var release = new ManualResetEventSlim();
        var row = new FakeCleanupProvider("row") { WhilePlanning = () => clock.Advance(TimeSpan.FromSeconds(1)) };
        var holders = Enumerable.Range(0, CleanupPlanner.ProvidersAtOnce - 1)
            .Select(index => (ICleanupProvider)new FakeCleanupProvider($"holder{index}") { WhilePlanning = () => release.Wait(Deadline) });
        var queued = new FakeCleanupProvider("queued")
        {
            WhilePlanning = () =>
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                registrar.Answer([]);
                release.Set();
            },
        };
        var timed = new PlanTimeLog();

        await new CleanupPlanner([row, .. holders, queued, registrar], volumes: new FakeVolumeInventory(), time: clock)
            .PlanAllAsync(timed: timed)
            .WaitAsync(Deadline * 2);

        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(timed.Times, time => time.Provider == row).Elapsed);
    }

    /// <summary>
    /// A row that registers storage, absent so it plans nothing itself, whose reading answers only when a
    /// test says so.
    /// </summary>
    private sealed class GatedRegistrar(string id) : ICleanupProvider, IRegistersVirtualMachineStorage
    {
        private readonly TaskCompletionSource<IReadOnlyList<VirtualMachineStorage>> _answer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the registrar is first asked for its storage.</summary>
        public Task Asked => _asked.Task;

        public void Answer(IReadOnlyList<VirtualMachineStorage> storage) => _answer.SetResult(storage);

        public void Fail(Exception failure) => _answer.SetException(failure);

        public Task<IReadOnlyList<VirtualMachineStorage>> RegisteredStorageAsync(CancellationToken ct = default)
        {
            _asked.TrySetResult();
            return _answer.Task.WaitAsync(ct);
        }

        public string Id => id;

        public string Name => id;

        public SafetyTier Tier => SafetyTier.UserData;

        public StepGrain Grain => StepGrain.Parts;

        public string WhatHappensOnNextUse => "Nothing.";

        public ProviderBasis Basis => ProviderBasis.Observed;

        public ProviderDescription Description { get; } = new()
        {
            Category = ProviderCategory.TemporaryFiles,
            Application = "A stub, standing in for a virtualisation tool.",
            Publisher = "Nobody.",
            Purpose = "Nothing. This provider exists only for this test.",
            Recommendation = "Nothing to recommend.",
        };

        public bool IsAwaitingSourceFolders => false;

        public IReadOnlyList<ToolRoot> ToolRoots => [];

        public Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolRoot>>([]);

        public Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CleanedPlace>>([]);

        public Task<bool> IsPresentAsync(CancellationToken ct = default) => Task.FromResult(false);

        public void InvalidateCaches()
        {
        }

        public PassHold HoldCaches() => PassHold.Nothing;

        public Task BeginPass(CancellationToken ct) => Task.CompletedTask;

        public Task<CleanupPlan> PlanAsync(MinimumAge keep = default, IProgress<ScanSize>? counted = null, CancellationToken ct = default) =>
            throw new NotSupportedException("This stub is absent, so it is never asked to plan.");

        public Task<CleanupResult> ExecuteAsync(
            CleanupPlan plan,
            RunReach? runReach = null,
            RunResidue? residue = null,
            IProgress<double>? progress = null,
            IProgress<CommandUnderway?>? underway = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This stub only registers storage.");

        public Task<VerificationResult> VerifyAsync(
            CleanupPlan plan,
            RunReach? runReach = null,
            RunResidue? residue = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This stub only registers storage.");
    }
}
