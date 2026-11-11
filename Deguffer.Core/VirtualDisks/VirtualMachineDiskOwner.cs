namespace Deguffer.Core.VirtualDisks;

/// <summary>A virtual machine as its hypervisor's records describe it, for the route its disks are given.</summary>
/// <param name="Name">The machine's name, as the hypervisor shows it.</param>
/// <param name="Checkpoints">
/// How many snapshots or checkpoints the records hold, or null where they could not be read, which is
/// never taken to be none.
/// </param>
/// <param name="IsSaved">
/// Whether the machine is suspended or saved, so its memory is kept on the drive beside its disks.
/// </param>
public sealed record VirtualMachineRecord(string Name, int? Checkpoints, bool IsSaved);

/// <summary>What one disk is to its machine, which decides what a reader is told about it.</summary>
public enum MachineDiskRole
{
    /// <summary>A disk the machine runs from now: the newest in its chain.</summary>
    Current,

    /// <summary>
    /// A disk a snapshot or checkpoint keeps: the machine as it was, which the disks it runs from now are
    /// built on, and which the hypervisor merges away only when that snapshot is deleted.
    /// </summary>
    Checkpoint,
}

/// <summary>
/// A disk of a hypervisor's virtual machine. The sentences every hypervisor shares are here, and each
/// hypervisor's own words and commands are in its record, so the three reports read alike and none
/// can name another's route.
///
/// <para>Every route is two steps and says so (§5.4): delete the snapshots inside the hypervisor, which
/// merges their disks, then compact the disk with the machine off. Deleting a snapshot's disk by path
/// instead leaves a machine that does not start and loses every write since the snapshot, which every
/// vendor's own documentation warns against.</para>
/// </summary>
/// <param name="Machine">The machine the disk belongs to.</param>
/// <param name="Role">What the disk is to the machine.</param>
public abstract record VirtualMachineDiskOwner(VirtualMachineRecord Machine, MachineDiskRole Role) : DiskOwner
{
    /// <summary>The hypervisor, as its vendor names it in a sentence, such as "Hyper-V".</summary>
    protected abstract string Hypervisor { get; }

    /// <summary>The vendor's word for a snapshot, in the singular: "snapshot" or "checkpoint".</summary>
    protected abstract string Snapshot { get; }

    /// <summary>
    /// How the vendor deletes the snapshots it no longer needs, as one sentence or more, which comes first in
    /// the route of a machine that has any.
    /// </summary>
    protected abstract string DeleteSnapshots { get; }

    /// <summary>How the vendor compacts the disk once no snapshot holds it, as one sentence or more.</summary>
    protected abstract string Compact { get; }

    /// <summary>What becomes of the saved memory of a suspended or saved machine, as one sentence.</summary>
    protected abstract string Saved { get; }

    /// <summary>The vendor's route as the clause that ends Explore's one-sentence verdict.</summary>
    protected abstract string RouteClause { get; }

    public sealed override string Name(VirtualDisk disk) => Role switch
    {
        MachineDiskRole.Checkpoint => $"a {Snapshot} of the {Hypervisor} virtual machine '{Machine.Name}'",
        _ => $"the {Hypervisor} virtual machine '{Machine.Name}'",
    };

    public sealed override string Summary(VirtualDisk disk) => Role switch
    {
        MachineDiskRole.Checkpoint =>
            $"A virtual disk a {Snapshot} of the {Hypervisor} virtual machine '{Machine.Name}' keeps: the machine as "
            + $"it was when the {Snapshot} was taken, which the disks it runs from now are built on. {Hypervisor} "
            + $"merges it away when the {Snapshot} is deleted, and the machine does not start without it.",
        _ =>
            $"A virtual disk of the {Hypervisor} virtual machine '{Machine.Name}': the machine's own drive, "
            + "everything installed and saved in it included. It grows as the machine writes and does not shrink by "
            + "itself. " + VirtualDiskRoutes.InsideIsNotOnTheDrive,
    };

    public sealed override string Route(VirtualDisk disk, bool isSparse)
    {
        var route = Machine.Checkpoints switch
        {
            0 => $"To make it smaller, delete what is no longer needed inside the machine first. {Compact}",

            null => $"Whether the machine has {Snapshot}s could not be read. Delete any it no longer needs first: "
                + $"{DeleteSnapshots} {Compact}",

            int count => $"The machine has {Count(count)}, and this disk is part of their chain, so never delete it "
                + $"by itself. To make the machine smaller, delete the {Snapshot}s it no longer needs first: "
                + $"{DeleteSnapshots} {Compact}",
        };

        return Machine.IsSaved ? $"{route} {Saved}" : route;
    }

    public sealed override string RouteInOneSentence(VirtualDisk disk) =>
        $"Deleting it breaks the machine, and the supported way to make it smaller is {RouteClause}.";

    private string Count(int count) => count == 1 ? $"1 {Snapshot}" : $"{count} {Snapshot}s";
}
