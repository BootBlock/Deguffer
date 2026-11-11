namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// A disk of a VMware Workstation virtual machine, or one of its snapshots' disks.
///
/// <para>Broadcom: "If your disk has snapshots, you must delete the snapshots before trying to reduce the
/// size of the disk", shrinking "does not apply to pre-allocated disks", and a shrink must never be
/// interrupted (KB 315631). The command-line shrink is <c>vmware-vdiskmanager -k</c>, for a growable disk
/// only.</para>
/// </summary>
public sealed record VmwareDisk(VirtualMachineRecord Machine, MachineDiskRole Role) : VirtualMachineDiskOwner(Machine, Role)
{
    protected override string Hypervisor => "VMware Workstation";

    protected override string Snapshot => "snapshot";

    protected override string DeleteSnapshots =>
        "delete each in Workstation's Snapshot Manager, or with 'vmrun deleteSnapshot', which merges its disk into "
        + "the next.";

    protected override string Compact =>
        "Then shut the machine down and use VM > Manage > Clean up Disks, or 'vmware-vdiskmanager -k' on the disk, "
        + "and let it finish. A pre-allocated disk cannot be made smaller.";

    protected override string Saved =>
        "The machine is suspended, so its memory is kept on the drive until it is resumed and shut down.";

    protected override string RouteClause =>
        "to delete its snapshots in Workstation's Snapshot Manager and then use Clean up Disks with the machine off";
}
