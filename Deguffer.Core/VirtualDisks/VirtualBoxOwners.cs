namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// A disk of a VirtualBox virtual machine, or one of its snapshots' disks.
///
/// <para>The manual: <c>VBoxManage snapshot delete</c> merges a snapshot's differencing images into their
/// children, and <c>modifymedium --compact</c> works on "VDI images" only, after "a suitable software tool"
/// has zeroed the free space inside the guest (the VBoxManage reference).</para>
/// </summary>
public sealed record VirtualBoxDisk(VirtualMachineRecord Machine, MachineDiskRole Role) : VirtualMachineDiskOwner(Machine, Role)
{
    protected override string Hypervisor => "VirtualBox";

    protected override string Snapshot => "snapshot";

    protected override string DeleteSnapshots =>
        $"delete each in VirtualBox's Snapshots list, or with 'VBoxManage snapshot {Quoted(Machine.Name)} delete', which "
        + "merges its disk into the next.";

    protected override string Compact =>
        "Then zero the free space inside the guest, shut the machine down and run 'VBoxManage modifymedium disk' with "
        + "'--compact' on the disk, which VirtualBox documents for VDI images only.";

    protected override string Saved =>
        "The machine is in a saved state, so its memory is kept on the drive until it is started and shut down, or the "
        + "saved state is discarded in VirtualBox.";

    protected override string RouteClause =>
        "to delete its snapshots in VirtualBox and then compact a VDI disk with 'VBoxManage modifymedium --compact' while "
        + "the machine is off";

    private static string Quoted(string name) => name.Contains(' ') ? $"\"{name}\"" : name;
}

/// <summary>
/// A disk VirtualBox has registered that no machine uses now, nor any of its snapshots: one a machine
/// still lists after it was detached, or one the global media registry lists for no machine at all. It
/// may hold data somebody needs, and VirtualBox's own Virtual Media Manager is where it is released.
/// <c>VBoxManage closemedium --delete</c> deletes the image, so no route here names it.
/// </summary>
/// <param name="Machine">The machine whose records list it, or null for one only the global registry lists.</param>
public sealed record VirtualBoxMedium(string? Machine) : DiskOwner
{
    public override string Name(VirtualDisk disk) => Machine is { } machine
        ? $"a disk the VirtualBox virtual machine '{machine}' lists and does not use"
        : "a disk registered with VirtualBox that no virtual machine uses";

    public override string Summary(VirtualDisk disk) =>
        $"A virtual disk VirtualBox has registered, {(Machine is { } machine ? $"listed by the virtual machine '{machine}'" : "listed for no virtual machine")}, "
        + "which no machine uses now. It may still hold data somebody needs.";

    public override string Route(VirtualDisk disk, bool isSparse) =>
        "VirtualBox's Virtual Media Manager lists it, and is where it is released, or removed if nothing in it is needed.";

    public override string RouteInOneSentence(VirtualDisk disk) =>
        "No machine uses it now, and VirtualBox's Virtual Media Manager is where it is released, or removed once nothing "
        + "in it is needed.";
}
