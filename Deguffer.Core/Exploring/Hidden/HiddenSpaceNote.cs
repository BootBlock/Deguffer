using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring.Hidden;

/// <summary>
/// What the reader is told about the space Windows states the size of and no folder holds: what it
/// is, and how Windows itself reduces it.
///
/// <para>The route is Windows' own and never an offer. Explore never classifies and acts on nothing
/// it did not draw as a thing on the disk (§7.1), and neither block is one: restore points go
/// through System Protection's limit and reserved storage through its setting.</para>
/// </summary>
public static class HiddenSpaceNote
{
    private const string ShadowCopiesReduced =
        "\nWindows reduces it through System Protection's limit on how much of the drive restore "
        + "points may use. Lowering it can delete older restore points.";

    private const string Reserved =
        "Space Windows keeps back so that updates can still be downloaded and installed when the drive "
        + "is nearly full. Windows counts it apart from every file, so no folder holds it."
        + "\nWindows reduces it through its reserved storage setting, which an administrator turns off "
        + "with 'DISM.exe /Online /Set-ReservedStorageState /State:Disabled'. Windows refuses while an "
        + "update is using the space.";

    /// <summary>The note for the block standing for restore points and shadow copies.</summary>
    public static string ShadowCopies(ShadowStorage storage) =>
        "Space Windows has set aside on this drive for restore points and shadow copies, kept in "
        + "System Volume Information, which the scan could not open. "
        + Holding(storage)
        + ShadowCopiesReduced;

    /// <summary>The note for the block standing for reserved storage.</summary>
    public static string ReservedStorage() => Reserved;

    /// <summary>
    /// What to add to the description of <paramref name="node"/>: Windows' figure for the shadow
    /// copy storage, where the node is the volume's <c>System Volume Information</c> and the scan
    /// counted the storage inside it, so it is drawn there rather than as a block of its own. Empty
    /// for every other node.
    /// </summary>
    public static string For(ExploreTree tree, int node, VolumeSpace volume)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return volume.CountedSystemVolumeInformation
            && volume.Hidden.ShadowCopies is { Statement: Statement.Stated, AllocatedBytes: > 0 } storage
            && tree.ParentOf(node) == tree.RootNode
            && node != tree.RootNode
            && string.Equals(tree.NameOf(node), VolumeSpace.SystemVolumeInformation, StringComparison.OrdinalIgnoreCase)
                ? $"Windows states that {FreeSpace.Format(storage.AllocatedBytes)} of this is set aside for "
                  + "restore points and shadow copies. "
                  + Holding(storage)
                  + ShadowCopiesReduced
                : string.Empty;
    }

    private static string Holding(ShadowStorage storage) =>
        $"{FreeSpace.Format(storage.UsedBytes)} of it holds them now, "
        + (storage.MaximumBytes is { } maximum
            ? $"and System Protection lets it grow to {FreeSpace.Format(maximum)}."
            : "and System Protection sets no limit on it.");
}
