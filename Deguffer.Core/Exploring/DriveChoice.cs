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
public sealed record DriveChoice(string RootPath, string? Label, long? TotalBytes, long? FreeBytes)
{
    /// <summary>What the machine reported about <paramref name="volume"/>, as an entry.</summary>
    public static DriveChoice From(LocalVolume volume) =>
        new(volume.RootPath, volume.Label, volume.TotalBytes, volume.FreeBytes);

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
    /// </summary>
    public string Description => Label is null ? $"{RootPath}, {Sizes}" : $"{RootPath} {Label}, {Sizes}";
}
