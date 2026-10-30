namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// The tool a virtual disk belongs to, which decides what the disk holds, what a reader calls it and the
/// route that makes it smaller.
///
/// <para><b>One record per owning tool (G1).</b> A container tool that runs on WSL registers
/// distributions of its own, and each one recognises its own by name (<see cref="ClaimsDistribution"/>),
/// so a new tool is a new record and one line in <see cref="WslRegistrations"/>, never an edit to a
/// switch. The text shared by every route is in <see cref="VirtualDiskRoutes"/>, so the Storage page and
/// Explore cannot come to disagree.</para>
/// </summary>
public abstract record DiskOwner
{
    /// <summary>
    /// Whether a WSL distribution of this name is this tool's. False for an owner that registers none
    /// under a name of its own.
    /// </summary>
    public virtual bool ClaimsDistribution(string name) => false;

    /// <summary>Whose the disk is, as a reader knows it and in the shape a sentence needs.</summary>
    public abstract string Name(VirtualDisk disk);

    /// <summary>What the disk holds, for Explore's summary of the file.</summary>
    public abstract string Summary(VirtualDisk disk);

    /// <summary>
    /// The route to a smaller disk, in the order its steps are taken, as one or more sentences for the
    /// Storage page's report.
    /// </summary>
    /// <param name="isSparse">Whether the file is sparse, where that is known.</param>
    public abstract string Route(VirtualDisk disk, bool isSparse);

    /// <summary>The route as one sentence, for Explore's verdict line, which states its point on one line.</summary>
    public abstract string RouteInOneSentence(VirtualDisk disk);
}

/// <summary>
/// A WSL distribution the user installed or imported: its whole Linux file system. WSL's own commands
/// are the route. Every distribution no other tool claims is this.
/// </summary>
public sealed record WslDistributionOwner : DiskOwner
{
    public static WslDistributionOwner Instance { get; } = new();

    public override string Name(VirtualDisk disk) => $"the WSL distribution '{disk.Distribution}'";

    public override string Summary(VirtualDisk disk) =>
        $"The virtual disk of the WSL distribution '{disk.Distribution}': its whole Linux file system, home "
        + "folders included. It grows as files are written inside Linux and does not shrink by itself. "
        + VirtualDiskRoutes.InsideIsNotOnTheDrive;

    public override string Route(VirtualDisk disk, bool isSparse) =>
        $"To make it smaller, delete what is no longer needed inside '{disk.Distribution}' first."
        + VirtualDiskRoutes.WslCompaction(disk, isSparse);

    public override string RouteInOneSentence(VirtualDisk disk) =>
        "Deleting it deletes the distribution and everything in it, and the supported way to make it "
        + "smaller is to delete files inside Linux and then compact it with "
        + $"'wsl --manage {VirtualDiskRoutes.Quoted(disk.Distribution)} --compact'.";
}
