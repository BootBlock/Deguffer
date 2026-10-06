using System.Text;
using Deguffer.Core.Exploring.Hidden;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// What the block for space in use that a scan did not count can be made of, most likely first. A
/// block with a size and no explanation reads as a fault in the scan, and every item here is
/// something the reader can check or act on.
///
/// <para>Where Windows stated the size of a part, the note says so rather than listing it as
/// something the block might hold. A part drawn as a block of its own is named as left out of this
/// one, with its figure, so the figure is still stated where its own block was too thin to draw. A
/// part Windows refused to state is named with what would state it.</para>
/// </summary>
public static class ExploreUnaccountedNote
{
    private const string Opening =
        "Windows says this much of the drive is in use beyond what the scan counted.";

    private const string Unelevated =
        "\n• Folders this scan was not allowed to open, such as other accounts' files. Scan as "
        + "administrator to count most of them.";

    private const string Elevated = "\n• Folders even an administrator's scan cannot open.";

    private const string ShadowCopies =
        "\n• Restore points and shadow copies, which Windows keeps in System Volume Information.";

    private const string ShadowCopiesRefused =
        "\n• Restore points and shadow copies, which Windows keeps in System Volume Information. "
        + "Windows states their size only to an administrator, so scan as administrator to see it.";

    private const string Reserved = "\n• Space Windows keeps back for updates (reserved storage).";

    private const string ReservedRefused =
        "\n• Space Windows keeps back for updates (reserved storage). Windows states its size only "
        + "to an administrator, so scan as administrator to see it.";

    private const string Causes =
        "\n• The file system's own records: the file table, its journals and the free-space map."
        + "\n• Rounding: each file takes whole clusters, so many small files use more than their sizes."
        + "\n• Files deleted while a program still has them open. The space comes back when it closes them."
        + "\n• A disk quota, which makes Windows report less free space to this account.";

    /// <summary>
    /// The note for the unaccounted block beside a scan that counted <paramref name="scannedBytes"/>
    /// of <paramref name="volume"/>, made with or without administrator rights.
    ///
    /// <para>Two versions of the first cause, because it is the one an elevated scan fixes. Offering
    /// that fix to a scan that is already elevated would send the reader round in a circle.</para>
    /// </summary>
    public static string For(bool isElevated, VolumeSpace volume, long scannedBytes)
    {
        var parts = volume.Parts(scannedBytes);
        var note = new StringBuilder(Opening);

        if (parts.ShadowCopies > 0 || parts.Reserved > 0)
        {
            note.Append(" Not included, because Windows states their size and they are drawn as blocks of their own:");
            Figure(parts.ShadowCopies, "Restore points and shadow copies");
            Figure(parts.Reserved, "Reserved storage");
        }

        note.Append(" It can include:");
        note.Append(isElevated ? Elevated : Unelevated);

        if (parts.ShadowCopies == 0 && !volume.CountedSystemVolumeInformation)
        {
            note.Append(Cause(volume.Hidden.ShadowCopies.Statement, volume.ShadowCopiesBeside, ShadowCopies, ShadowCopiesRefused));
        }

        if (parts.Reserved == 0)
        {
            note.Append(Cause(volume.Hidden.Reserved.Statement, volume.Hidden.Reserved.Bytes, Reserved, ReservedRefused));
        }

        return note.Append(Causes).ToString();

        void Figure(long bytes, string what)
        {
            if (bytes > 0)
            {
                note.Append($"\n• {what}: {FreeSpace.Format(bytes)}.");
            }
        }
    }

    /// <summary>
    /// A part Windows names that was not drawn on its own. Stated at zero, it is not a cause and is
    /// left out. Stated and not drawn, it did not fit in the space the scan left over, and its
    /// figure is given rather than a difference worked out from it.
    /// </summary>
    private static string Cause(Statement statement, long bytes, string cause, string refused) => statement switch
    {
        Statement.NeedsElevation => refused,
        Statement.Stated when bytes <= 0 => string.Empty,
        Statement.Stated => $"{cause[..^1]}: Windows states {FreeSpace.Format(bytes)}.",
        _ => cause,
    };
}
