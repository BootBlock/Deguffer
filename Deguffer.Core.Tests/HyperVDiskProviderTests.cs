using Deguffer.Core.Execution;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.VirtualDisks;
using Deguffer.Core.Wmi;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Hyper-V's machines, disks and checkpoints, as Hyper-V's own WMI namespace lists them, reported with
/// Hyper-V's route and no step, and an answer Hyper-V would not give read as unknown, never as no machines.
///
/// <para>The namespace is a <see cref="FakeHyperV"/> holding a recorded answer, and nothing is started:
/// every provider is given a <see cref="FakeProcessRunner"/> with no reply, which throws for any program.
/// Every disk is a file in the test's own folder, and the account is a member of Hyper-V Administrators
/// unless a test says otherwise.</para>
/// </summary>
public sealed class HyperVDiskProviderTests : IDisposable
{
    /// <summary><c>EnabledState</c> for a machine that is turned off.</summary>
    private const int Off = 3;

    /// <summary><c>EnabledState</c> 6, which the Hyper-V module calls <c>Saved</c>.</summary>
    private const int Saved = 6;

    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;
    private readonly FakeSystemDirectories _system;
    private readonly HyperVMachines _machines;

    public HyperVDiskProviderTests()
    {
        _environment = new FakeUserEnvironment(_temp.Path).AsHyperVAdministrator();
        _system = new FakeSystemDirectories(_temp.Path);
        _machines = new HyperVMachines(_system, _environment, new FakeHyperV());
    }

    public void Dispose() => _temp.Dispose();

    private HyperVDiskProvider Provider(FakeHyperV hyperV, IUserEnvironment? environment = null) =>
        new(_system, environment ?? _environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning, hyperV: hyperV);

    private void InstallHyperV()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_machines.Service)!);
        File.WriteAllBytes(_machines.Service, []);
    }

    private string Disk(string name, int bytes = 4096)
    {
        var folder = Path.Combine(_temp.Path, "Hyper-V", "Dev");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static string Text(CleanupPlan plan) => string.Join("\n", plan.Notes.Select(note => note.Message));

    [Fact]
    public async Task WithoutHyperVNothingIsAskedAndThereIsNoRow()
    {
        var hyperV = new FakeHyperV().Machine("Dev", Off, [Disk("Dev.vhdx")]);

        Assert.False(await Provider(hyperV).IsPresentAsync());
        Assert.Equal(0, hyperV.Opens);
    }

    /// <summary>
    /// <c>Get-VM</c> has been reported (by Altaro) to list nothing, rather than fail, for an account Hyper-V does not
    /// trust, so an account that is neither elevated nor a member of Hyper-V Administrators is told whether it
    /// has machines is not known, and Hyper-V is never asked what the token already says.
    /// Mutation: dropping the role check asks Hyper-V, and taking the refusal for no machines leaves no row.
    /// </summary>
    [Fact]
    public async Task AnAccountHyperVWillNotAnswerIsToldItIsNotKnownWithNothingAsked()
    {
        InstallHyperV();
        var hyperV = new FakeHyperV();
        var provider = Provider(hyperV, new FakeUserEnvironment(_temp.Path));

        Assert.True(await provider.IsPresentAsync());

        var plan = await provider.PlanAsync();
        var refusal = Assert.Single(plan.Notes, note => note.Severity == PlanNoteSeverity.Warning);

        Assert.Contains("elevated PowerShell or a member of the Hyper-V Administrators group", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("is not known", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(plan.Steps);
        Assert.Empty(await provider.RegisteredStorageAsync());
        Assert.Equal(0, hyperV.Opens);
    }

    /// <summary>
    /// The two accounts Hyper-V answers are asked, an elevated one and an unelevated member of Hyper-V
    /// Administrators. Mutation: checking only <see cref="IUserEnvironment.IsAdministrator"/> refuses the member.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnElevatedAccountOrAMemberOfHyperVAdministratorsIsAsked(bool elevated)
    {
        InstallHyperV();
        var account = new FakeUserEnvironment(_temp.Path);
        var environment = elevated ? account.AsAdministrator() : account.AsHyperVAdministrator();
        var disk = Disk("Dev.vhdx");
        var hyperV = new FakeHyperV().Machine("Dev", Off, [disk]);

        Assert.Equal(disk, Assert.Single(await Provider(hyperV, environment).RegisteredStorageAsync()).Path);
        Assert.Equal(1, hyperV.Opens);
        Assert.Equal(1, hyperV.Disposals);
    }

    /// <summary>
    /// Each way the namespace can fail to answer is said, with why, and leaves the machines unknown rather
    /// than none. Mutation: returning an empty inventory for any of them leaves no row.
    /// </summary>
    [Theory]
    [InlineData("open", unchecked((int)0x80041003), "refused to list its virtual machines to this account")]
    [InlineData("open", unchecked((int)0x8004100E), "would not list its virtual machines (error 0x8004100E)")]
    [InlineData("systems", unchecked((int)0x80070005), "refused to list its virtual machines to this account")]
    [InlineData("settings", WbemConnection.Unfinished, "did not list its virtual machines within 2 minutes")]
    public async Task AHyperVThatDoesNotAnswerIsSaidAndNeverTakenForNoMachines(string failing, int hr, string said)
    {
        InstallHyperV();
        var hyperV = new FakeHyperV
        {
            OpenError = failing == "open" ? hr : WbemConnection.S_OK,
            SystemsError = failing == "systems" ? hr : WbemConnection.S_OK,
            SettingsError = failing == "settings" ? hr : WbemConnection.S_OK,
        }.Machine("Dev", Off, [Disk("Dev.vhdx")]);
        var provider = Provider(hyperV);

        Assert.True(await provider.IsPresentAsync());

        var text = Text(await provider.PlanAsync());
        Assert.Contains(said, text, StringComparison.Ordinal);
        Assert.Contains("is not known", text, StringComparison.Ordinal);
        Assert.Empty(await provider.RegisteredStorageAsync());
    }

    /// <summary>A service file Windows will not describe may be there, so Hyper-V is asked anyway.</summary>
    [Fact]
    public async Task AServiceWindowsWillNotDescribeIsAskedRatherThanTakenForAbsent()
    {
        InstallHyperV();
        using var denied = DeniedDirectory.WithUnreadableFile(_machines.Service);
        var hyperV = new FakeHyperV();

        await Provider(hyperV).IsPresentAsync();

        Assert.Equal(1, hyperV.Opens);
    }

    [Fact]
    public async Task AnAccountWithNoMachinesHasNoRow()
    {
        InstallHyperV();

        Assert.False(await Provider(new FakeHyperV()).IsPresentAsync());
    }

    /// <summary>
    /// A machine with checkpoints: the disk it runs from, the checkpoint's disk under it, both measured and
    /// registered, and Hyper-V's route in order, checkpoints first.
    /// </summary>
    [Fact]
    public async Task AMachinesDiskAndItsCheckpointsDiskAreReportedWithHyperVsRouteInOrder()
    {
        InstallHyperV();
        var current = Disk("Dev_7F3A.avhdx", 8192);
        var base_ = Disk("Dev.vhdx", 4096);
        var hyperV = new FakeHyperV().Disk(current, parent: base_).Machine("Dev", Saved, [current], [base_], [current]);
        var provider = Provider(hyperV);

        var plan = await provider.PlanAsync();
        var text = Text(plan);

        Assert.True(plan.ReportsOnly);
        Assert.Empty(plan.Steps);
        Assert.Contains($"The Hyper-V virtual machine 'Dev': {current}.", text, StringComparison.Ordinal);
        Assert.Contains($"A checkpoint of the Hyper-V virtual machine 'Dev': {base_}.", text, StringComparison.Ordinal);
        Assert.Contains($"2 virtual disks take {FreeSpace.Format(12288)} on this drive.", text, StringComparison.Ordinal);
        Assert.Contains("The machine has 2 checkpoints", text, StringComparison.Ordinal);
        Assert.Contains("The machine is saved", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("Remove-VMCheckpoint", StringComparison.Ordinal) < text.IndexOf("Optimize-VHD", StringComparison.Ordinal),
            "The route deletes checkpoints before it compacts the disk.");
        Assert.Contains(VirtualDiskRoutes.HyperVModuleNeeds, text, StringComparison.Ordinal);

        var registered = (await provider.RegisteredStorageAsync()).Select(storage => storage.Path).ToList();
        Assert.Equal([current, base_], registered);
    }

    /// <summary>
    /// The inventory a recorded answer gives: two machines, one with two checkpoints on two branches,
    /// each built on a chain down to the base disk, one whose chain Hyper-V cuts short. A disk on the
    /// branch the machine no longer runs from is reached only through its checkpoint's disk. It is the inventory the
    /// Windows PowerShell query gave for the same machines, disks and chains, which listed the attached
    /// disks as the machine's, every checkpoint's disk and every parent as its checkpoints', each once,
    /// and the first disk it could not describe. Mutation: following only the attached disks loses the
    /// other branch's disk, and taking a disk Hyper-V would not describe for one built on nothing hides the cut chain.
    /// </summary>
    [Fact]
    public void TheInventoryFromARecordedAnswerListsEveryDiskOfEveryChainAndSaysWhereOneIsCut()
    {
        var root = Path.Combine(_temp.Path, "Hyper-V");
        string At(string name) => Path.Combine(root, name);

        var hyperV = new FakeHyperV()
            .Disk(At("Dev_B.avhdx"), parent: At("Dev_A.avhdx"))
            .Disk(At("Dev_A.avhdx"), parent: At("Dev.vhdx"))
            .Disk(At("Dev_X.avhdx"), parent: At("Dev_W.avhdx"))
            .Disk(At("Dev_W.avhdx"), parent: At("Dev.vhdx"))
            .Disk(At("Dev.vhdx"))
            .Answering(At("Build_1.avhdx"), new HyperVDiskAnswer(WbemConnection.S_OK, 32769, null))
            .Machine("Dev", Off, [At("Dev_B.avhdx")], [At("Dev_A.avhdx")], [At("Dev_X.avhdx")])
            .Machine("Build", Saved, [At("Build_1.avhdx")], [At("Build_1.avhdx")]);

        var inventory = new HyperVMachines(_system, _environment, hyperV).Read(CancellationToken.None);

        var dev = new VirtualMachineRecord("Dev", 2, false);
        var build = new VirtualMachineRecord("Build", 1, true);

        Assert.Equal(
            [
                (At("Build_1.avhdx"), new HyperVDisk(build, MachineDiskRole.Current)),
                (At("Dev_B.avhdx"), new HyperVDisk(dev, MachineDiskRole.Current)),
                (At("Dev_A.avhdx"), new HyperVDisk(dev, MachineDiskRole.Checkpoint)),
                (At("Dev_X.avhdx"), new HyperVDisk(dev, MachineDiskRole.Checkpoint)),
                (At("Dev.vhdx"), new HyperVDisk(dev, MachineDiskRole.Checkpoint)),
                (At("Dev_W.avhdx"), new HyperVDisk(dev, MachineDiskRole.Checkpoint)),
            ],
            inventory.Disks.Select(disk => (disk.Path, (HyperVDisk)disk.Owner)));
        Assert.All(inventory.Disks, disk => Assert.Null(disk.Distribution));
        Assert.Equal(
            [
                $"Hyper-V would not describe {At("Build_1.avhdx")}, a disk of the virtual machine 'Build', so the disks it is "
                + "built on are not listed here.",
            ],
            inventory.Problems);
    }

    /// <summary>
    /// Only a realized machine is listed, with only its virtual hard disks: the host and a planned
    /// machine have no realized settings, and a DVD image is the user's file, which
    /// <c>Get-VMHardDiskDrive</c> does not list. Every kind of checkpoint counts, as <c>Get-VMSnapshot</c>
    /// counts them. Mutation: dropping the type check lists the planned machine's disk.
    /// </summary>
    [Fact]
    public void OnlyARealizedMachinesHardDisksAreListedAndEveryKindOfCheckpointIsCounted()
    {
        var disk = Path.Combine(_temp.Path, "Dev.vhdx");
        var recovery = Path.Combine(_temp.Path, "Dev_R.avhdx");
        var planned = Path.Combine(_temp.Path, "Planned.vhdx");
        var image = Path.Combine(_temp.Path, "setup.iso");

        var hyperV = new FakeHyperV()
            .Disk(recovery)
            .Disk(planned)
            .System(new HyperVSystemRow("HOST", "HOST", 2))
            .System(new HyperVSystemRow("PLANNED-ID", "Planned", Off))
            .Settings(new HyperVSettingsRow("PLANNED-ID", "Microsoft:Hyper-V:System:Planned", [new("Microsoft:Hyper-V:Virtual Hard Disk", planned)]))
            .System(new HyperVSystemRow("DEV-ID", "Dev", 2))
            .Settings(new HyperVSettingsRow(
                "DEV-ID",
                "Microsoft:Hyper-V:System:Realized",
                [new("Microsoft:Hyper-V:Virtual Hard Disk", disk), new("Microsoft:Hyper-V:Virtual CD/DVD Disk", image)]))
            .Settings(new HyperVSettingsRow("DEV-ID", "Microsoft:Hyper-V:Snapshot:Recovery", [new("Microsoft:Hyper-V:Virtual Hard Disk", recovery)]))
            .Settings(new HyperVSettingsRow("DEV-ID", "Microsoft:Hyper-V:Snapshot:Replica:Standard", []))
            .Disk(disk);

        var inventory = new HyperVMachines(_system, _environment, hyperV).Read(CancellationToken.None);

        Assert.Equal([disk, recovery], inventory.Disks.Select(found => found.Path));
        Assert.Equal(new VirtualMachineRecord("Dev", 2, false), Assert.IsType<HyperVDisk>(inventory.Disks[0].Owner).Machine);
        Assert.Empty(inventory.Problems);
    }

    /// <summary>
    /// A parent two disks share is described once, and a chain that comes back on itself ends rather than
    /// holding the reading. Mutation: dropping the record of what was described asks about the base twice,
    /// and dropping the chain's own set never returns.
    /// </summary>
    [Fact]
    public void EachDiskIsDescribedOnceAndAChainThatComesBackOnItselfEnds()
    {
        string At(string name) => Path.Combine(_temp.Path, name);

        var hyperV = new FakeHyperV()
            .Disk(At("A.avhdx"), parent: At("Base.vhdx"))
            .Disk(At("B.avhdx"), parent: At("Base.vhdx"))
            .Disk(At("Base.vhdx"))
            .Disk(At("Loop1.avhdx"), parent: At("Loop2.avhdx"))
            .Disk(At("Loop2.avhdx"), parent: At("Loop1.avhdx"))
            .Machine("One", Off, [At("A.avhdx")])
            .Machine("Two", Off, [At("B.avhdx"), At("Loop1.avhdx")]);

        var inventory = new HyperVMachines(_system, _environment, hyperV).Read(CancellationToken.None);

        Assert.Single(hyperV.Described, path => path == At("Base.vhdx"));
        Assert.Equal(hyperV.Described.Count, hyperV.Described.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(inventory.Disks, disk => disk.Path == At("Loop2.avhdx"));
        Assert.Empty(inventory.Problems);
    }

    /// <summary>
    /// A path Hyper-V gives with the extended-length prefix is the same disk as its plain form, so it is
    /// named once and compares equal to the path Explore draws.
    /// </summary>
    [Fact]
    public void ADiskHyperVNamesWithTheDevicePrefixIsThePlainPath()
    {
        var plain = Path.Combine(_temp.Path, "Hyper-V", "Dev", "Dev.vhdx");
        var hyperV = new FakeHyperV().Disk(plain).Disk(@"\\?\" + plain).Machine("Dev", Off, [@"\\?\" + plain], [plain]);

        var inventory = new HyperVMachines(_system, _environment, hyperV).Read(CancellationToken.None);

        var disk = Assert.Single(inventory.Disks);
        Assert.Equal(plain, disk.Path);
        Assert.False(disk.Path.StartsWith(@"\\?\", StringComparison.Ordinal));
        Assert.Equal(MachineDiskRole.Current, Assert.IsType<HyperVDisk>(disk.Owner).Role);
    }

    /// <summary>
    /// Every way a disk's description can fail cuts its chain, and the cut is said: a call WMI failed, a
    /// return value that is not completed, and a description with no <c>ParentPath</c> in it. Mutation:
    /// reading any of them as a disk built on nothing leaves no problem.
    /// </summary>
    [Theory]
    [InlineData(unchecked((int)0x80041010), null, null)]
    [InlineData(0, 4096, null)]
    [InlineData(0, 0, "<INSTANCE CLASSNAME=\"Msvm_VirtualHardDiskSettingData\"></INSTANCE>")]
    public void AParentHyperVWouldNotDescribeIsSaidRatherThanHidden(int error, int? returnValue, string? settingData)
    {
        var disk = Path.Combine(_temp.Path, "Dev.avhdx");
        var hyperV = new FakeHyperV()
            .Answering(disk, new HyperVDiskAnswer(error, returnValue, settingData))
            .Machine("Dev", Off, [disk]);

        var inventory = new HyperVMachines(_system, _environment, hyperV).Read(CancellationToken.None);

        Assert.Contains(disk, Assert.Single(inventory.Problems), StringComparison.Ordinal);
        Assert.Equal(disk, Assert.Single(inventory.Disks).Path);
    }

    /// <summary>
    /// A reading cancelled while Hyper-V's service is answering stops waiting at once, and the session is
    /// still released once the service answers. Mutation: awaiting the work without the token waits for
    /// the service, which never answers here until the test lets it.
    /// </summary>
    [Fact]
    public async Task ACancelledReadingStopsWaitingForHyperVAndStillReleasesTheSession()
    {
        InstallHyperV();
        using var answering = new ManualResetEventSlim();
        using var asked = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        var hyperV = new FakeHyperV
        {
            Describing = (_, _) =>
            {
                asked.Set();
                answering.Wait(TimeSpan.FromSeconds(30));
            },
        }.Machine("Dev", Off, [Disk("Dev.vhdx")]);

        var held = new HyperVMachines(_system, _environment, hyperV).ReadAsync(cancel.Token);

        Assert.True(asked.Wait(TimeSpan.FromSeconds(30)), "the reading never reached the disk's description");
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
        Assert.Equal(0, hyperV.Disposals);

        answering.Set();
        SpinWait.SpinUntil(() => hyperV.Disposals == 1, TimeSpan.FromSeconds(30));
        Assert.Equal(1, hyperV.Disposals);
    }

    /// <summary>
    /// The run-wide refusal for a Hyper-V disk: a row whose location holds one leaves it, and the run's §5.6
    /// negative names it as having survived. Only that row is scanned, so neither may depend on this row.
    /// </summary>
    [Fact]
    public async Task ARowWhoseLocationHoldsAHyperVDiskLeavesItAndTheRunProvesIt()
    {
        InstallHyperV();
        var caches = Path.Combine(_environment.UserProfile, ".gradle", "caches");
        var machines = Path.Combine(caches, "vms");
        Directory.CreateDirectory(machines);
        var disk = Path.Combine(machines, "Dev.vhdx");
        File.WriteAllBytes(disk, new byte[4096]);
        File.WriteAllBytes(Path.Combine(caches, "modules.bin"), new byte[4096]);

        var gradle = new GradleCacheProvider(_environment, new FakeProcessRunner(), FakeProcessInspector.NothingRunning);
        Assert.Contains((await gradle.PlanAsync()).TargetedPaths, path => LongPath.Contains(path, disk));

        var report = Provider(new FakeHyperV().Machine("Dev", Off, [disk]));
        var planner = new CleanupPlanner([gradle, report], volumes: new FakeVolumeInventory());
        var cache = Assert.Single(await planner.PlanAsync([gradle]));

        Assert.DoesNotContain(cache.Plan!.TargetedPaths, path => LongPath.Contains(path, disk));

        var results = await planner.ExecuteAsync([cache]);

        Assert.True(File.Exists(disk), "a row whose location held a Hyper-V disk deleted it");
        Assert.All(results, result => Assert.True(result.Verification!.Passed, result.Verification.Summary));
        Assert.Contains(
            results,
            result => result.ProviderId == report.Id
                && result.Verification!.Checks.Any(check => check.Subject == disk && check.Outcome == VerificationOutcome.Survived));
    }
}
