using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Tests;

/// <summary>
/// The run-wide refusal of a virtual machine's storage, applied to a finished plan: which steps are
/// withheld, which are left alone, and the protections that let §5.6 prove each disk survived.
///
/// <para>Driven against plans and registrations built here rather than through a provider, because the
/// rule is the pass's and not any row's. <see cref="VirtualDiskProviderTests"/> covers a real row's disk
/// through the planner and a run.</para>
/// </summary>
public sealed class VirtualMachineStoragePlanTests
{
    private const string Imported = @"C:\Users\testuser\AppData\Local\Temp\distros\ubuntu";

    private const string Disk = Imported + @"\ext4.vhdx";

    private const string Registrar = "virtual-disks";

    private static CleanupPlan Planning(string providerId = "test", params CleanupStep[] steps) => new()
    {
        ProviderId = providerId,
        ProviderName = "Test",
        Tier = SafetyTier.RegenerableCache,
        WhatHappensOnNextUse = "Nothing.",
        Steps = steps,
    };

    private static RegisteredStorage Registered(string path = Disk, PathPresence presence = PathPresence.Present) =>
        new([new RegisteredEntry(new VirtualMachineStorage(path, "the WSL distribution 'Ubuntu'"), Registrar, presence, path)]);

    [Fact]
    public void APlanWhoseStepsReachNoStorageIsReturnedAsItIs()
    {
        var plan = Planning(steps: new DeleteDirectoryStep(@"C:\Users\testuser\AppData\Local\Temp\distros\notes", "Notes"));

        Assert.Same(plan, VirtualMachineStoragePlan.Apply(plan, Registered()));
        Assert.Same(plan, VirtualMachineStoragePlan.Apply(plan, RegisteredStorage.None));
    }

    /// <summary>
    /// A removal whose subject holds a registered disk is withheld whole: the disk is the machine, and what
    /// lies beside it in that folder is its tool's too. The disk is protected so the run must prove it
    /// survived, the folder on its contents, and the preview says why the row did less.
    /// </summary>
    [Fact]
    public void ARemovalHoldingADiskIsWithheldAndTheDiskAndItsFolderAreProtected()
    {
        var holding = new DeleteDirectoryStep(@"C:\Users\testuser\AppData\Local\Temp\distros", "Temporary files")
        {
            Estimated = new ScanSize(4096, 4096),
        };
        var beside = new DeleteDirectoryStep(@"C:\Users\testuser\AppData\Local\Temp\build-1", "Temporary files");

        var applied = VirtualMachineStoragePlan.Apply(Planning(steps: [holding, beside]), Registered());

        Assert.Equal([beside], applied.Steps);
        Assert.DoesNotContain(applied.TargetedPaths, path => LongPath.Contains(path, Disk));

        var disk = Assert.Single(applied.ProtectedPaths, p => p.Path == Disk);
        Assert.Equal(Withholding.VirtualMachineStorage, disk.Withheld);
        Assert.Equal(PathPresence.Present, disk.PresenceBefore);
        Assert.Contains("the WSL distribution 'Ubuntu'", disk.Reason, StringComparison.Ordinal);

        var folder = Assert.Single(applied.ProtectedPaths, p => p.Path == holding.Path);
        Assert.True(folder.HeldContentBefore);

        var note = Assert.Single(applied.Notes, n => n.Severity == PlanNoteSeverity.Warning);
        Assert.Contains(holding.Path, note.Message, StringComparison.Ordinal);
        Assert.Contains(Disk, note.Message, StringComparison.Ordinal);
        Assert.True(applied.HoldsVirtualMachineStorage);
    }

    /// <summary>A step that names the disk itself is withheld, and only the disk is protected.</summary>
    [Fact]
    public void AFileStepNamingTheDiskIsWithheld()
    {
        var applied = VirtualMachineStoragePlan.Apply(Planning(steps: new DeleteFileStep(Disk, "Old file")), Registered());

        Assert.Empty(applied.Steps);
        Assert.Equal(Disk, Assert.Single(applied.ProtectedPaths).Path);
        Assert.True(applied.HasSomethingToProve);
    }

    /// <summary>
    /// A tool's own command decides what it removes (§5.1), so one sent where a disk is cannot be told to
    /// leave it, and is withheld rather than run.
    /// </summary>
    [Fact]
    public void ACommandSentWhereADiskIsIsWithheld()
    {
        var command = new RunCommandStep("tool.exe", "cache clean", "The tool's cache")
        {
            WhenCancelled = CancelledCommand.Ended,
            MeasuredPaths = [@"C:\Users\testuser\AppData\Local\Temp"],
        };

        var applied = VirtualMachineStoragePlan.Apply(Planning(steps: command), Registered());

        Assert.Empty(applied.Steps);
        Assert.Contains(applied.Notes, n => n.Message.Contains("tool.exe cache clean", StringComparison.Ordinal));
        Assert.Contains(applied.ProtectedPaths, p => p.Path == Disk && p.Withheld == Withholding.VirtualMachineStorage);
    }

    /// <summary>
    /// A registered folder is the machine's whole: a step inside it is withheld as one holding it is. This
    /// is the shape a hypervisor's virtual machine folder registers in.
    /// </summary>
    [Fact]
    public void AStepInsideARegisteredFolderIsWithheld()
    {
        var inside = new DeleteDirectoryStep(Imported + @"\cache", "Cache");

        var applied = VirtualMachineStoragePlan.Apply(Planning(steps: inside), Registered(Imported));

        Assert.Empty(applied.Steps);
        Assert.Equal(Imported, Assert.Single(applied.ProtectedPaths).Path);
    }

    /// <summary>
    /// A disk Windows would not describe may be there, so it is refused as one that is. A disk Windows
    /// reports absent cannot be deleted, and a stale record must not hold a folder back for ever.
    /// </summary>
    [Fact]
    public void ARefusedDiskIsProtectedAndAnAbsentOneIsNot()
    {
        var plan = Planning(steps: new DeleteDirectoryStep(Imported, "Folder"));

        var refused = VirtualMachineStoragePlan.Apply(plan, Registered(presence: PathPresence.Refused));
        Assert.Empty(refused.Steps);
        Assert.Equal(PathPresence.Refused, Assert.Single(refused.ProtectedPaths, p => p.Path == Disk).PresenceBefore);

        Assert.Same(plan, VirtualMachineStoragePlan.Apply(plan, Registered(presence: PathPresence.Absent)));
    }

    /// <summary>
    /// The registering row's own plan protects every disk it registered, though it has no step, so a run
    /// proves them whichever rows it cleans. Another row with nothing in reach gains nothing.
    /// </summary>
    [Fact]
    public void TheRegisteringRowsPlanProtectsEveryDiskItRegistered()
    {
        var report = VirtualMachineStoragePlan.Apply(Planning(Registrar) with { ReportsOnly = true }, Registered());

        Assert.True(report.HasSomethingToProve);
        Assert.Equal(Disk, Assert.Single(report.ProtectedPaths, p => p.Withheld == Withholding.VirtualMachineStorage).Path);
        Assert.Equal(Disk, Assert.Single(report.ProofOnly().ProtectedPaths).Path);

        var other = Planning();
        Assert.Same(other, VirtualMachineStoragePlan.Apply(other, Registered()));
    }
}
