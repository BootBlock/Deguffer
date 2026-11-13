namespace Deguffer.Core.Diagnostics;

/// <summary>
/// A question to Windows that costs far more than reading a file's attributes, counted by
/// <see cref="MachineCallTally"/> so a planning pass can say where its time went rather than leave it
/// estimated from the code (#516).
/// </summary>
internal enum MachineCall
{
    /// <summary>
    /// <c>GetVolumePathNameW</c>: where the volume holding a path is mounted, asked where the path's
    /// own folders do not prove it. Its cost grows with the path's depth.
    /// </summary>
    VolumePathName,

    /// <summary><c>GetVolumeNameForVolumeMountPointW</c>: which volume a mount point names now.</summary>
    VolumeNameOfMountPoint,

    /// <summary><c>GetVolumePathNamesForVolumeNameW</c>: every place a volume is mounted.</summary>
    MountPointsOfVolume,

    /// <summary>One Restart Manager session, asking who holds a set of files open.</summary>
    RestartManagerSession,

    /// <summary><c>GetFinalPathNameByHandleW</c>: the normalised path an open handle is on.</summary>
    FinalPathName,

    /// <summary>One reading of the process table: every process, its image and its working directory.</summary>
    ProcessTable,
}
