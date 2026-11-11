namespace Deguffer.Core.Health;

/// <summary>
/// What a place that lists a volume says about the drives under it, beside how full it is (§7.6). A
/// drive that is filling wants a clean and a drive that is failing wants a backup first, so a drive
/// of concern is named where the free space is.
/// </summary>
/// <param name="Verdict">The worst verdict among the volume's drives: Watch or Failing.</param>
/// <param name="Word">The verdict in one word, so the note never depends on a colour or a glyph (§6.5).</param>
/// <param name="Text">One sentence naming the drives of concern and what to do first.</param>
public sealed record VolumeHealthNote(DriveVerdict Verdict, string Word, string Text)
{
    /// <summary>
    /// The note for a volume on <paramref name="drives"/>, or null where none of them is a concern.
    /// A drive with no problems reported, or with no report at all, adds nothing: Health says which,
    /// and a line beside every volume saying all is well would soon be read past.
    /// </summary>
    public static VolumeHealthNote? For(IEnumerable<DriveHealth> drives)
    {
        var concerns = drives.Where(drive => drive.Verdict.IsConcern()).ToList();

        if (concerns.Count == 0)
        {
            return null;
        }

        var worst = concerns.Max(drive => drive.Verdict);
        var named = concerns.Where(drive => drive.Verdict == worst).Select(drive => drive.Name).Distinct().ToList();
        var one = named.Count == 1;

        return worst is DriveVerdict.Failing
            ? new VolumeHealthNote(
                worst,
                worst.Word(),
                $"{Names(named)} {(one ? "reports" : "report")} a serious problem. Back up what is on this drive before cleaning it or anything else.")
            : new VolumeHealthNote(
                worst,
                worst.Word(),
                $"{Names(named)} {(one ? "has a reading that needs" : "have readings that need")} attention. Back up important files, and see Health for the details.");
    }

    private static string Names(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
}
