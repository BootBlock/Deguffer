using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring;

/// <summary>
/// One volume as the Explore drive picker offers it: where it is mounted, what it is called, and
/// how much of it is in use.
///
/// <para>A picker that lists mount points alone asks the reader to remember which letter is the
/// large disk and which is the card reader. The four figures the entry carries are the ones that
/// answer that without opening anything.</para>
///
/// <para>Here rather than on the page because the wording is the part worth asserting, and a test
/// can reach Core. <see cref="LocalVolume"/> stays what the machine reported. This is that reading
/// worded for a reader, which is a second responsibility and so a second type (G1).</para>
/// </summary>
/// <param name="RootPath">
/// Where it is mounted, in <c>D:\</c> or <c>C:\Mount\</c> form. What a scan is pointed at.
///
/// <para>One entry per volume rather than per mount point, taken from
/// <see cref="LocalVolume.RootPath"/>: a volume mounted at both a letter and a folder is one drive
/// to the reader, and listing it twice would ask them which of the two to scan when the answer is
/// the same either way. A volume with no letter at all is listed under its folder mount point,
/// which is the name the user sees it under in File Explorer, and is refused there — see
/// <see cref="NoDriveLetterRefusal"/>.</para>
/// </param>
/// <param name="Label">What the volume is called, or null where it has none.</param>
/// <param name="TotalBytes">Capacity, or null where the volume would not say.</param>
/// <param name="FreeBytes">What is left of that capacity, or null where the volume would not say.</param>
/// <param name="Refusal">
/// Why Explore will not scan this volume, or null where it will. Carried rather than derived from a
/// flag at the page, so the one sentence a reader is given is asserted here with the rest of the
/// wording.
/// </param>
public sealed record DriveChoice(
    string RootPath, string? Label, long? TotalBytes, long? FreeBytes, string? Refusal = null)
{
    /// <summary>
    /// What a volume whose contents are not on this machine is told. See
    /// <see cref="LocalVolume.StoresContentRemotely"/> for how one is recognised.
    ///
    /// <para>It names the consequence rather than the mechanism. "Unsupported volume" would leave a
    /// reader trying the folder picker on the same drive, and the cost of succeeding at that is a
    /// download of everything they keep in the cloud onto a disk they are cleaning because it is
    /// full.</para>
    /// </summary>
    public const string RemoteStorageRefusal =
        "Deguffer does not scan this drive. It is cloud storage that Windows shows as an ordinary "
        + "drive or folder, and reading it would download every file on it onto this computer.";

    /// <summary>
    /// What a volume mounted at a folder rather than under a drive letter is told.
    ///
    /// <para><b>A withheld capability rather than a hazard in the volume.</b> Such a volume is
    /// ordinary and reading it is safe. What is not yet safe is what Explore would then offer to
    /// delete on it: <c>ExploreActionPolicy</c> keeps a volume's paging file, its
    /// <c>System Volume Information</c>, its NTFS records and its Recycle Bin out of every deletion
    /// by asking whether a path is a direct child of its own volume root, and
    /// <see cref="Safety.VolumeRoot"/> answers that from the path's drive letter. On a volume
    /// mounted at <c>C:\Mount</c> the first segment below the root reads as <c>Mount</c>, so none
    /// of those refusals fire and another account's deleted files would be drawn as an ordinary
    /// folder.</para>
    ///
    /// <para>So the entry is listed and refused rather than hidden (§7.1), and the refusal is
    /// removed when that rule can name a volume by where it is really mounted. The folder picker
    /// reaches such a volume exactly as it did before, which is the arrangement this preserves
    /// rather than removes.</para>
    /// </summary>
    public const string NoDriveLetterRefusal =
        "Deguffer does not scan this drive. It is mounted at a folder rather than under a drive "
        + "letter, and the rules that keep a volume's own system files out of a deletion cannot yet "
        + "recognise one mounted that way.";

    /// <summary>
    /// What the machine reported about <paramref name="volume"/>, as an entry.
    ///
    /// <para>Remote storage is tested first where both apply, because it is the refusal with the
    /// larger consequence behind it: one withholds a picture of a disk, the other prevents a
    /// download of everything the user keeps in the cloud.</para>
    /// </summary>
    public static DriveChoice From(LocalVolume volume) =>
        new(
            volume.RootPath,
            volume.Label,
            volume.TotalBytes,
            volume.FreeBytes,
            RefusalFor(volume));

    private static string? RefusalFor(LocalVolume volume)
    {
        if (volume.StoresContentRemotely)
        {
            return RemoteStorageRefusal;
        }

        // A drive letter is its own path root and nothing else is, which is the same test
        // ShellRecycleBinEmptier.Serves applies for its own reason.
        return string.Equals(Path.GetPathRoot(volume.RootPath), volume.RootPath, StringComparison.OrdinalIgnoreCase)
            ? null
            : NoDriveLetterRefusal;
    }

    /// <summary>
    /// Whether a scan may be pointed at this volume.
    ///
    /// <para>The entry is offered either way. A volume the user can see in File Explorer, missing
    /// from a list of drives to scan, is a volume they cannot reason about — so it is listed, it is
    /// refused, and the refusal says why (§7.1).</para>
    /// </summary>
    public bool IsRefused => Refusal is not null;

    /// <summary>
    /// What is in use, or null where either half of the subtraction is unknown. Derived rather than
    /// carried, because a used figure that disagreed with the two it came from would be a bug the
    /// reader could see.
    /// </summary>
    public long? UsedBytes => TotalBytes - FreeBytes;

    /// <summary>The label, or an empty string, because a binding cannot show null.</summary>
    public string LabelText => Label ?? string.Empty;

    /// <summary>
    /// The three space figures in one phrase, or a plain statement that the volume did not say.
    /// A dash would read as zero.
    /// </summary>
    public string Sizes =>
        UsedBytes is { } used && FreeBytes is { } free && TotalBytes is { } total
            ? $"{FreeSpace.Format(used)} used, {FreeSpace.Format(free)} free of {FreeSpace.Format(total)}"
            : "size unknown";

    /// <summary>
    /// Everything the entry shows, in one sentence, for the screen reader. A templated combo box
    /// item otherwise announces its parts in layout order with no wording between them.
    ///
    /// <para>The refusal is part of that sentence rather than a second announcement. A reader who
    /// hears the mount point and the sizes and then finds Scan unavailable has been told the two
    /// things that do not matter and none of the one that does.</para>
    /// </summary>
    public string Description =>
        (Label is null ? $"{RootPath}, {Sizes}" : $"{RootPath} {Label}, {Sizes}")
        + (Refusal is null ? string.Empty : $". {Refusal}");
}
