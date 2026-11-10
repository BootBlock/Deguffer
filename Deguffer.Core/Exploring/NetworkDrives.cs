namespace Deguffer.Core.Exploring;

/// <summary>
/// Whether a <see cref="DriveList"/> offers the drive letters Windows maps to network shares.
///
/// <para>The two pickers that read the drives want opposite answers. Explore pictures a share by
/// walking it, and refuses only to remove anything on one. The duplicate search removes what it
/// finds, and a file on a share can be one of this computer's own files under a second name, so a
/// mapped letter is nothing it can search.</para>
/// </summary>
public enum NetworkDrives
{
    /// <summary>Mapped letters are not in the list.</summary>
    LeftOut,

    /// <summary>Mapped letters are listed beside the local drives, and are scanned like them.</summary>
    Offered,
}
