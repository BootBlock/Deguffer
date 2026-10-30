using Deguffer.Core.VirtualDisks;

namespace Deguffer.Core.Providers;

/// <summary>
/// A row that reads a virtualisation tool's own records, and so knows where that tool keeps each
/// machine's disks and folders.
///
/// <para><b>Registering is the whole of what a report does to protect them.</b>
/// <see cref="Execution.CleanupPlanner"/> asks every row that implements this before a planning pass,
/// withholds any step of any row that would reach what it names, and proves after every clean that each
/// one is still there (§5.6). Explore refuses each one too
/// (<see cref="Exploring.Acting.ExploreActionPolicy.ForAsync"/>). A new report adds its machines by
/// implementing this, and nothing else changes.</para>
/// </summary>
public interface IRegistersVirtualMachineStorage
{
    /// <summary>
    /// Every disk and folder the tool's records name, whether or not it is there. Asked once before a
    /// planning pass and once before a clean, so an implementation keeps its reading for the pass (G4).
    /// </summary>
    Task<IReadOnlyList<VirtualMachineStorage>> RegisteredStorageAsync(CancellationToken ct = default);
}
