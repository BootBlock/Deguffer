namespace Deguffer.Core.Health;

/// <summary>
/// The one statement of which verdicts are a concern and what each is called, so the Health page,
/// its headline and the places that list volumes cannot disagree about a drive.
/// </summary>
public static class DriveVerdicts
{
    /// <summary>Whether the verdict needs the reader's attention. Unknown is not: it is the absence of a judgement.</summary>
    public static bool IsConcern(this DriveVerdict verdict) => verdict is DriveVerdict.Watch or DriveVerdict.Failing;

    /// <summary>The verdict in one word, so it never depends on a colour or a glyph (§6.5).</summary>
    public static string Word(this DriveVerdict verdict) => verdict switch
    {
        DriveVerdict.Good => "Good",
        DriveVerdict.Watch => "Watch",
        DriveVerdict.Failing => "Failing",
        _ => "Unknown",
    };
}
