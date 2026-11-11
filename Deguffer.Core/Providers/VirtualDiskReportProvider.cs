using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;
using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Providers;

/// <summary>
/// A row that reports the virtual disks one kind of tool records, and offers nothing (§5.4, and
/// <c>docs/todo/unreached-locations.md</c> §11). Each derived row reads its own tool's records, and
/// this holds what every such row shares: the reading kept for the pass, the registration, and the
/// report.
///
/// <para><b>Found from the tool's own records.</b> A search for <c>.vhdx</c>, <c>.vmdk</c> or
/// <c>.vdi</c> files would find disks no tool owns, and could not say which route applies to the ones
/// it found.</para>
///
/// <para><b>No step.</b> Compacting a disk that is in use or sparse can damage the whole disk, and
/// deleting a snapshot's or checkpoint's file by path breaks the machine, so the route is named and
/// left to the vendor's commands, in the order they are taken. Nothing here starts a machine or a
/// hypervisor.</para>
///
/// <para><b>Everything registered, so no run deletes it.</b> The row is how the planner learns where the
/// disks are, and the machine files where the tool's records name them
/// (<see cref="IRegistersVirtualMachineStorage"/>): a step of any other row
/// that would reach one is withheld, and every clean proves afterwards that each one is still there.</para>
///
/// <para><b>Tier 3</b>, because a disk deleted is a distribution or a machine gone for good. The tier
/// describes the disk. The row offers nothing to tick either way.</para>
/// </summary>
public abstract class VirtualDiskReportProvider : CleanupProviderBase, IRegistersVirtualMachineStorage
{
    /// <summary>What the tool records, read once per planning pass and shared by presence and planning (G4).</summary>
    private readonly PassMemo<Task<VirtualDiskInventory>> _inventory = new();

    protected VirtualDiskReportProvider(
        IUserEnvironment environment, IProcessRunner runner, IProcessInspector inspector, IDirectoryScanner? scanner)
        : base(environment, runner, inspector, scanner)
    {
    }

    public sealed override SafetyTier Tier => SafetyTier.UserData;

    public sealed override StepGrain Grain => StepGrain.Parts;

    public sealed override string WhatHappensOnNextUse =>
        "Nothing changes: Deguffer only reports these disks. Making one smaller is done with the commands the "
        + "report names, inside the tool that owns the disk.";

    public override void InvalidateCaches()
    {
        _inventory.Forget();
        base.InvalidateCaches();
    }

    /// <summary>A disk the tool records, or a record that could not be read.</summary>
    public sealed override async Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var inventory = await InventoryAsync(ct).ConfigureAwait(false);

        return inventory.Disks.Count > 0 || inventory.Problems.Count > 0;
    }

    /// <summary>
    /// Every disk and machine file the tool records, from the same reading the plan makes. Explore
    /// refuses each one, and a run refuses and proves each one, through this alone.
    /// </summary>
    public async Task<IReadOnlyList<VirtualMachineStorage>> RegisteredStorageAsync(CancellationToken ct = default) =>
        (await InventoryAsync(ct).ConfigureAwait(false)).Storage();

    /// <summary>
    /// None: the row only reports how large each disk is, and its plan holds no step at all.
    /// </summary>
    public sealed override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([]);

    protected sealed override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var inventory = await InventoryAsync(ct).ConfigureAwait(false);

        var measured = inventory.Disks.Select(disk => (Disk: disk, Size: VirtualDiskFile.Measure(disk))).ToList();

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            ReportsOnly = true,

            // No protected paths: every clean proves each disk this row registers, whether or not this row
            // was scanned or chosen (VirtualMachineStoragePlan.ProofFor).
            Notes = VirtualDiskReport.Notes(measured, await InsideAsync(measured, ct).ConfigureAwait(false), inventory.Problems),
        };
    }

    /// <summary>
    /// Reads the tool's records afresh. Asked at most once per planning pass, with the token of whoever
    /// asked first.
    /// </summary>
    protected abstract Task<VirtualDiskInventory> ReadInventoryAsync(CancellationToken ct);

    /// <summary>
    /// What the tool says is reclaimable inside its disks, or null where it says nothing. Only Docker
    /// Desktop's own accounting gives such a figure.
    /// </summary>
    protected virtual Task<DockerInsideReading?> InsideAsync(
        IReadOnlyList<(VirtualDisk Disk, VirtualDiskSize Size)> measured, CancellationToken ct) =>
        Task.FromResult<DockerInsideReading?>(null);

    private Task<VirtualDiskInventory> InventoryAsync(CancellationToken ct) => _inventory.Get(() => ReadInventoryAsync(ct));
}
