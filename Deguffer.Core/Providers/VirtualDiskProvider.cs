using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Providers;

/// <summary>
/// The virtual disks WSL and Docker Desktop keep their Linux file systems in, reported and never acted
/// on (§5.4, §11).
///
/// <para><b>Found from the tools' own records.</b> WSL's registration names each distribution's disk,
/// and Docker Desktop's settings name its data disk. A search for <c>.vhdx</c> files would find disks no
/// tool owns, and could not say which route applies to the ones it found.</para>
///
/// <para><b>Two figures, kept apart.</b> The host file's size on the drive, from the file system, and
/// what is reclaimable inside, from Docker's own accounting where Docker Desktop is already running.
/// Freeing the second does not free the first until the disk is compacted, and the report says so for
/// every disk, because a user who prunes and sees no change stops trusting the figure.</para>
///
/// <para><b>No step.</b> Compacting a disk that is in use or sparse can damage the whole disk, so the
/// route is named and left to the vendor's commands, in the order they are taken. Nothing here starts
/// WSL or the Docker engine, and the one command it can run only reads.</para>
///
/// <para><b>Tier 3</b>, because a disk deleted is a distribution, or every image, container and volume,
/// gone for good. The tier describes the disk. The row offers nothing to tick either way.</para>
/// </summary>
public sealed class VirtualDiskProvider : CleanupProviderBase
{
    private readonly ISystemDirectories _system;

    private readonly DockerInsideFigure _docker;

    /// <summary>What the tools record, read once per planning pass and shared by presence and planning (G4).</summary>
    private VirtualDiskInventory? _inventory;

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
            scanner ?? DirectoryScanner.Default)
    {
        _system = system ?? SystemDirectories.Current;
        _docker = new DockerInsideFigure(Environment, Runner, Inspector);
    }

    public override string Id => "virtual-disks";

    public override string Name => "WSL and Docker virtual disks";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Nothing changes: Deguffer only reports these disks. Making one smaller is done with the commands the "
        + "report names, inside the tool that owns the disk.";

    public override ProviderDescription Description { get; } = new()
    {
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

    public override void InvalidateCaches()
    {
        _inventory = null;
        base.InvalidateCaches();
    }

    /// <summary>A disk WSL or Docker Desktop records, or a record that could not be read.</summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var inventory = Inventory();

        return Task.FromResult(inventory.Disks.Count > 0 || inventory.Problems.Count > 0);
    }

    /// <summary>
    /// Explore refuses each disk and the folder it is in, and nothing else beside it: the folder can be
    /// a drive root a distribution was imported to, and what else is there is the user's.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. Inventory().Disks
                .Where(disk => Path.GetDirectoryName(disk.Path) is { Length: > 0 })
                .Select(disk => ToolRoot.Sparing(
                    Path.GetDirectoryName(disk.Path)!,
                    $"The virtual disk of {disk.Owner}. Deleting it deletes everything in it.",
                    [Path.GetFileName(disk.Path)])),
        ]);

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var inventory = Inventory();

        var measured = inventory.Disks.Select(disk => (Disk: disk, Size: VirtualDiskFile.Measure(disk.Path))).ToList();

        var inside = measured.Any(entry => entry.Disk.Kind == VirtualDiskKind.DockerData
                && entry.Size.Presence == PathPresence.Present)
            ? await _docker.ReadAsync(ct).ConfigureAwait(false)
            : null;

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            ReportsOnly = true,

            // §5.6: nothing here removes a disk, and a run that did would be a rule gone wrong elsewhere.
            ProtectedPaths = Protect(
            [
                .. measured
                    .Where(entry => entry.Size.Presence != PathPresence.Absent)
                    .Select(entry => (entry.Disk.Path, $"The virtual disk of {entry.Disk.Owner}.")),
            ]),
            Notes = VirtualDiskReport.Notes(measured, inside, inventory.Problems),
        };
    }

    private VirtualDiskInventory Inventory() => _inventory ??= VirtualDiskInventory.Read(Environment, _system);
}
