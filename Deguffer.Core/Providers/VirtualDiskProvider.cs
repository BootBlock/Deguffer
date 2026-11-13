using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Providers;

/// <summary>
/// The virtual disks WSL and Docker Desktop keep their Linux file systems in, reported and never acted
/// on. What every disk report shares is in <see cref="VirtualDiskReportProvider"/>.
///
/// <para><b>Found from the tools' own records.</b> WSL's registration names each distribution's disk,
/// and Docker Desktop's settings name its data disk.</para>
///
/// <para><b>Two figures, kept apart.</b> The host file's size on the drive, from the file system, and
/// what is reclaimable inside, from Docker's own accounting where Docker Desktop is already running.
/// Freeing the second does not free the first until the disk is compacted, and the report says so for
/// every disk, because a user who prunes and sees no change stops trusting the figure. Nothing here
/// starts WSL or the Docker engine, and the one command it can run only reads.</para>
/// </summary>
public sealed class VirtualDiskProvider : VirtualDiskReportProvider
{
    private readonly ISystemDirectories _system;

    private readonly DockerInsideFigure _docker;

    /// <summary>Docker's own figure, or null where it was not asked, read once per planning pass.</summary>
    private readonly PassMemo<Task<DockerInsideReading?>> _inside = new();

    public VirtualDiskProvider(
        ISystemDirectories? system = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner)
    {
        _system = system ?? SystemDirectories.Current;
        _docker = new DockerInsideFigure(Environment, Runner, Inspector);
    }

    public override string Id => "virtual-disks";

    public override string Name => "WSL and Docker virtual disks";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "Windows Subsystem for Linux (WSL) and Docker Desktop",
        Publisher = "Microsoft and Docker",
        Purpose = "WSL keeps each Linux distribution's files in one virtual disk file, and Docker Desktop keeps "
            + "its images, containers and volumes in another. These files grow as Linux writes and never shrink by "
            + "themselves, so deleting files inside Linux frees space inside the disk and none on the drive.",
        Recommendation = "Deguffer reports each disk's size on the drive and, where Docker Desktop is running, how "
            + "much Docker says is reclaimable inside, and names the vendor's commands in the order to use them. It "
            + "never compacts or deletes a disk, because compacting one in use, or a sparse one, can damage "
            + "everything in it.",
    };

    protected override Task<VirtualDiskInventory> ReadInventoryAsync(CancellationToken ct) =>
        Task.FromResult(VirtualDiskInventory.Read(Environment, _system));

    public override void InvalidateCaches()
    {
        _inside.Forget();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Docker's own figure asked as the pass begins, where Docker Desktop is running and its data disk is
    /// there: before <c>docker system df</c> answers, the engine works out the size of every image,
    /// container, volume and build cache it keeps, so the wait grows with what Docker holds.
    ///
    /// <para>The disks are read from WSL's registration and Docker Desktop's settings and measured from
    /// outside first, as the plan does, and the plan then reports those same figures. It takes no place
    /// under the pass's bound on tool queries: <c>docker</c> is a native program whose wait is the
    /// engine's work, not an interpreter loading.</para>
    /// </summary>
    public override Task BeginPass(PassStart start) =>
        start.AskAtOnce(async ct => await InsideAsync(await MeasuredAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false));

    /// <summary>
    /// Docker's own figure, asked only where Docker Desktop's data disk is there to ask about, once per
    /// planning pass. Every caller in a pass passes the pass's one measurement, so the first decides.
    /// </summary>
    protected override Task<DockerInsideReading?> InsideAsync(
        IReadOnlyList<(VirtualDisk Disk, VirtualDiskSize Size)> measured, CancellationToken ct) =>
        _inside.Get(async () =>
            measured.Any(entry => entry.Disk.Owner is DockerDesktopData && entry.Size.Presence == PathPresence.Present)
                ? await _docker.ReadAsync(ct).ConfigureAwait(false)
                : null);
}
