using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.History;

/// <summary>
/// What one Explore scan covered, as far as keeping it for a later comparison is concerned.
/// </summary>
public enum ScanCoverage
{
    /// <summary>A whole drive of this computer, or the volume mounted at the folder chosen.</summary>
    WholeDrive,

    /// <summary>A folder on a drive of this computer.</summary>
    Folder,

    /// <summary>
    /// A share, or a drive letter Windows maps to one, whole or in part. Windows names no volume for
    /// either that would tell one share's scans from another's, and the inventory does not ask a
    /// share its size (<see cref="VolumeReadiness.NotAsked"/>).
    /// </summary>
    Share,
}
