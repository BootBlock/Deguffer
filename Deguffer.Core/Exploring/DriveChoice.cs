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
/// <param name="RootPath">Where it is mounted, in <c>D:\</c> form. What a scan is pointed at.</param>
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
        "Deguffer does not scan this drive. It is cloud storage shown as a drive letter, and reading "
        + "it would download every file on it onto this computer.";

    /// <summary>What the machine reported about <paramref name="volume"/>, as an entry.</summary>
    public static DriveChoice From(LocalVolume volume) =>
        new(
            volume.RootPath,
            volume.Label,
            volume.TotalBytes,
            volume.FreeBytes,
            volume.StoresContentRemotely ? RemoteStorageRefusal : null);

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
