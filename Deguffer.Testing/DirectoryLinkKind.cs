namespace Deguffer.Testing;

/// <summary>
/// The two ways Windows lets one folder stand for another. Both carry
/// <see cref="FileAttributes.ReparsePoint"/>, and only their reparse tags tell them apart, so code
/// that tells a link by its tag can treat one as a link and walk straight through the other.
/// </summary>
public enum DirectoryLinkKind
{
    /// <summary><c>IO_REPARSE_TAG_SYMLINK</c>, what <c>mklink /D</c> makes.</summary>
    SymbolicLink,

    /// <summary>
    /// <c>IO_REPARSE_TAG_MOUNT_POINT</c>, what <c>mklink /J</c> makes. It needs no privilege, so it is
    /// the usual way a cache is moved to another drive.
    /// </summary>
    Junction,
}
