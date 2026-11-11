namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// A disk of a Hyper-V virtual machine: its <c>.vhdx</c>, or a checkpoint's <c>.avhdx</c>.
///
/// <para>Microsoft: "When you delete a checkpoint, Hyper-V merges the .avhdx and .vhdx files for you…
/// You should not delete the .avhdx files directly" (the Hyper-V checkpoints page). And
/// <c>Optimize-VHD</c> needs the disk "not attached or… attached in read-only mode", and does nothing for
/// a fixed disk (its cmdlet page). Both need what every Hyper-V cmdlet needs, which is in
/// <see cref="VirtualDiskRoutes.HyperVModuleNeeds"/>.</para>
/// </summary>
public sealed record HyperVDisk(VirtualMachineRecord Machine, MachineDiskRole Role) : VirtualMachineDiskOwner(Machine, Role)
{
    protected override string Hypervisor => "Hyper-V";

    protected override string Snapshot => "checkpoint";

    protected override string DeleteSnapshots =>
        "delete each in Hyper-V Manager, or with Remove-VMCheckpoint, which merges its disk into the next.";

    protected override string Compact =>
        "Then shut the machine down and compact the disk with Optimize-VHD, which does nothing for a fixed-size disk. "
        + $"Both {VirtualDiskRoutes.HyperVModuleNeeds}.";

    protected override string Saved =>
        "The machine is saved, so its memory is kept on the drive until it is started and shut down, or its saved "
        + "state is deleted in Hyper-V Manager.";

    protected override string RouteClause =>
        "to delete its checkpoints with Remove-VMCheckpoint and then compact the disk with Optimize-VHD while the "
        + "machine is off";
}
