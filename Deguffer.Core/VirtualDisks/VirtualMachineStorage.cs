using Deguffer.Core.Providers;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// A disk file or a folder a virtualisation tool has registered as a machine's storage, which no
/// clean may remove whichever row's location happens to hold it.
///
/// <para>Deleting one deletes a whole machine: a WSL distribution and everything in it, every Docker
/// image, container and volume, or a virtual machine. Nothing re-creates it, and the tool that owns it
/// goes on listing a machine whose storage is gone. So it is refused by every route, not only by the
/// report that found it. See <see cref="IRegistersVirtualMachineStorage"/>.</para>
/// </summary>
/// <param name="Path">The disk file or the folder, in display form, as the tool's own record names it.</param>
/// <param name="Owner">
/// Whose it is, as a reader knows it and in the shape a sentence needs, such as "the WSL distribution
/// 'Ubuntu'" or "Docker Desktop's data".
/// </param>
public sealed record VirtualMachineStorage(string Path, string Owner)
{
    /// <summary>
    /// Explore's refusal of this storage: the entry itself, at the folder it is in, and nothing beside
    /// it. The folder can be a drive root a distribution was imported to, and what else is there is the
    /// user's. Null for a path with no folder above it, which is a whole volume and refused already.
    /// </summary>
    public ToolRoot? Refusal() =>
        System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } folder
            ? ToolRoot.Sparing(
                folder,
                $"The storage of {Owner}. Deleting it deletes everything in it.",
                [System.IO.Path.GetFileName(Path)])
            : null;
}
