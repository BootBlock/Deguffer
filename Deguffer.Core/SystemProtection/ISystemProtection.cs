using Deguffer.Core.Exploring.Hidden;

namespace Deguffer.Core.SystemProtection;

/// <summary>
/// The shadow copy storage on one volume, in Windows' own figures. See <see cref="ShadowStorage"/>.
/// </summary>
/// <param name="Volume">The volume's mount point, ending in a separator.</param>
public sealed record VolumeShadowStorage(string Volume, ShadowStorage Storage);

/// <summary>
/// Windows' System Protection: its restore points, the shadow copies they are kept in, and the storage
/// those take, read and acted on through Windows' own interfaces.
///
/// <para>Behind an interface because every member reaches the machine, and one of them removes a restore
/// point. A test that reached the real one would plan against, and could remove, the restore points of
/// whoever ran the suite. <see cref="WindowsSystemProtection"/> is the machine's.</para>
///
/// <para>Every member blocks: each is a call into a system service in another process. Callers on a
/// thread that draws call them off it.</para>
/// </summary>
public interface ISystemProtection
{
    /// <summary>Every restore point System Restore lists, asked of the machine at the moment of the call.</summary>
    RestorePointListing ListRestorePoints();

    /// <summary>
    /// Ask System Restore to remove the restore point numbered <paramref name="sequenceNumber"/>. System
    /// Restore removes the shadow copies it keeps for it, and Deguffer never names one.
    /// </summary>
    RemovalAnswer RemoveRestorePoint(uint sequenceNumber);

    /// <summary>Every shadow copy on the machine, whoever made it.</summary>
    ShadowCopyListing ListShadowCopies();

    /// <summary>The shadow copy storage on each local fixed volume that Windows could be asked about.</summary>
    IReadOnlyList<VolumeShadowStorage> ReadStorage();
}
