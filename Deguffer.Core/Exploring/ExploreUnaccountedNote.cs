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
/// something the block might hold. A part taken out of this block is named as left out, with its
/// figure, which is stated here whether or not its own block had room to be drawn. A part Windows
/// refused to state is named with what would state it.</para>
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

    private const string Records =
        "\n• The file system's own records: the file table, its journals and the free-space map.";

    // Only a walk misses it. The file table states what each file occupies, whole clusters included.
    private const string Rounding =
        "\n• Rounding: each file takes whole clusters, so many small files use more than their sizes.";

    private const string Causes =
        "\n• Files deleted while a program still has them open. The space comes back when it closes them."
        + "\n• A disk quota, which makes Windows report less free space to this account.";

    /// <summary>
    /// The note for the unaccounted block beside a scan that counted <paramref name="scannedBytes"/>
    /// of <paramref name="volume"/>, made with or without administrator rights.
    ///
    /// <para>Two versions of the first cause, because it is the one an elevated scan fixes. Offering
    /// that fix to a scan that is already elevated would send the reader round in a circle.</para>
    ///
    /// <para>Rounding is a cause only on a walk, which takes most files at their length. The file
    /// table draws what each file occupies, so naming it there would send the reader after space the
    /// map already shows.</para>
    /// </summary>
    public static string For(bool isElevated, VolumeSpace volume, long scannedBytes, ScanStrategy strategy)
    {
        var parts = volume.Parts(scannedBytes);
        var note = new StringBuilder(Opening);

        if (parts.ShadowCopies > 0 || parts.Reserved > 0)
        {
            note.Append(" Not included, because Windows states their size:");
            Figure(parts.ShadowCopies, "Restore points and shadow copies");
            Figure(parts.Reserved, "Reserved storage");
        }

        // After a list, on a line of its own: run on, it reads as part of the last figure.
        note.Append(parts.ShadowCopies > 0 || parts.Reserved > 0 ? "\nIt can include:" : " It can include:");
        note.Append(isElevated ? Elevated : Unelevated);

        if (parts.ShadowCopies == 0 && !volume.CountedSystemVolumeInformation)
        {
            note.Append(Cause(volume.Hidden.ShadowCopies.Statement, volume.ShadowCopiesBeside, ShadowCopies, ShadowCopiesRefused));
        }

        if (parts.Reserved == 0)
        {
            // No refused version: Windows gives the reserve to any process.
            note.Append(Cause(volume.Hidden.Reserved.Statement, volume.Hidden.Reserved.Bytes, Reserved));
        }

        note.Append(Records);

        if (strategy != ScanStrategy.MasterFileTable)
        {
            note.Append(Rounding);
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
    /// What to say where the scan counted more than <paramref name="volume"/> has in use, so no
    /// unaccounted block is drawn, or null where it did not.
    ///
    /// <para>The causes depend on the route. A walk counts a file with several names once for each
    /// and a file Windows compressed itself at its length. The file table counts each file once at
    /// what it occupies, so only a drive that changed during the scan is left to explain it.</para>
    /// </summary>
    public static string? Overcount(VolumeSpace volume, long scannedBytes, ScanStrategy strategy)
    {
        var over = volume == VolumeSpace.None ? 0 : volume.Parts(scannedBytes).Overcounted;
        if (over <= 0)
        {
            return null;
        }

        var causes = strategy == ScanStrategy.MasterFileTable
            ? "Files written while the scan ran can do this."
            : "A walk counts a file with several names once for each, and a file Windows compressed "
              + "itself (CompactOS) at its full length.";

        return $"The scan counted {FreeSpace.Format(over)} more than Windows says is in use, so none "
            + $"of the drive is shown as not accounted for. {causes}";
    }

    /// <summary>
    /// A part Windows names that was not drawn on its own. Stated at zero, it is not a cause and is
    /// left out. Stated and not drawn, it did not fit in the space the scan left over, and its
    /// figure is given rather than a difference worked out from it.
    /// </summary>
    private static string Cause(Statement statement, long bytes, string cause, string? refused = null) => statement switch
    {
        Statement.NeedsElevation when refused is not null => refused,
        Statement.Stated when bytes <= 0 => string.Empty,
        Statement.Stated => $"{cause[..^1]}: Windows states {FreeSpace.Format(bytes)}.",
        _ => cause,
    };
}
