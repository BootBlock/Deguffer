namespace Deguffer.Core.Exploring;

/// <summary>
/// What the block for space in use that a scan did not count can be made of, most likely first. A
/// block with a size and no explanation reads as a fault in the scan, and every item here is
/// something the reader can check or act on.
/// </summary>
public static class ExploreUnaccountedNote
{
    private const string Opening =
        "Windows says this much of the drive is in use beyond what the scan counted. It can include:";

    private const string Causes =
        "\n• Restore points and shadow copies, which Windows keeps in System Volume Information."
        + "\n• Space Windows keeps back for updates (reserved storage)."
        + "\n• The file system's own records: the file table, its journals and the free-space map."
        + "\n• Rounding: each file takes whole clusters, so many small files use more than their sizes."
        + "\n• Files deleted while a program still has them open. The space comes back when it closes them."
        + "\n• A disk quota, which makes Windows report less free space to this account.";

    private const string Unelevated =
        Opening
        + "\n• Folders this scan was not allowed to open, such as other accounts' files. Scan as "
        + "administrator to count most of them."
        + Causes;

    private const string Elevated =
        Opening
        + "\n• Folders even an administrator's scan cannot open."
        + Causes;

    /// <summary>
    /// The note for a scan made with or without administrator rights.
    ///
    /// <para>Two versions, because the first cause is the one an elevated scan fixes. Offering that
    /// fix to a scan that is already elevated would send the reader round in a circle.</para>
    /// </summary>
    public static string For(bool isElevated) => isElevated ? Elevated : Unelevated;
}
