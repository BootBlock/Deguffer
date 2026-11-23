namespace Deguffer.Core.Providers;

/// <summary>
/// What the superseded-versions row tells the user about the Squirrel applications whose builds it
/// leaves alone, in its plan and in Explore's refusals.
/// </summary>
internal static class SquirrelSupersededPlanNotes
{
    /// <summary>Why Explore refuses a build of an installation that is running.</summary>
    public static string RunningBuildReason(SquirrelInstallation installation) =>
        $"{installation.Name} is running, so Deguffer is leaving the build it replaced "
        + "alone: an application that has just updated may still be reading the folder "
        + "beside the one it started from. Close it and this can go.";

    /// <summary>Why Explore refuses a build of an installation nothing could check was not running.</summary>
    public static string UncheckedBuildReason(SquirrelInstallation installation) =>
        $"Deguffer could not tell whether {installation.Name} is running, so it is leaving the build it "
        + "replaced alone: an application that has just updated may still be reading the folder beside "
        + "the one it started from.";

    /// <summary>
    /// The rest of the sentence about installations left alone for <paramref name="doubt"/>, after
    /// "Left every version of ... alone:".
    /// </summary>
    public static string WhyUnordered(SquirrelOrderDoubt doubt, bool one) => doubt switch
    {
        SquirrelOrderDoubt.UnreadableVersion => one
            ? "it has a build whose version number Deguffer could not read, so it cannot tell which "
              + "one is in use."
            : "they have a build whose version number Deguffer could not read, so it cannot tell "
              + "which one is in use.",
        SquirrelOrderDoubt.AmbiguousBuilds => one
            ? "it has two builds whose folder names differ only in case, so Deguffer cannot tell "
              + "which one is in use."
            : "they each have two builds whose folder names differ only in case, so Deguffer cannot "
              + "tell which ones are in use.",
        SquirrelOrderDoubt.UnfinishedUpdate => one
            ? "an update to it stopped before it finished, so the newest build in its folder may not "
              + "be the one it starts."
            : "updates to them stopped before they finished, so the newest build in each folder may "
              + "not be the one that application starts.",
        SquirrelOrderDoubt.UnfinishedUnknown => one
            ? "Windows would not say whether its last update finished, so Deguffer cannot tell which "
              + "build it starts."
            : "Windows would not say whether their last updates finished, so Deguffer cannot tell "
              + "which builds they start.",
        SquirrelOrderDoubt.IndexBehind => one
            ? "its record of which build to start does not lead to the newest one, so its last "
              + "update may not have finished."
            : "their records of which build to start do not lead to the newest ones, so their last "
              + "updates may not have finished.",
        SquirrelOrderDoubt.IndexUnorderable => one
            ? "its record of which build to start names a version Deguffer cannot order, so it "
              + "cannot tell which one is in use."
            : "their records of which build to start name a version Deguffer cannot order, so it "
              + "cannot tell which ones are in use.",
        _ => throw new ArgumentOutOfRangeException(
            nameof(doubt), doubt, "Ordered installations and unread indexes have sentences of their own."),
    };

    /// <summary>
    /// Application names for a sentence, in the form a reader expects rather than comma-separated
    /// throughout. Driving the real window is what catches a sentence that reads correctly only on
    /// a machine with one of something.
    /// </summary>
    public static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };
}
