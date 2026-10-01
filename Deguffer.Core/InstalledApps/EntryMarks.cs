namespace Deguffer.Core.InstalledApps;

/// <summary>
/// What an entry's row shows beside its name (§7.3): a badge for its standing that opens the reason,
/// a badge where Windows hides it, a shield where an action on it needs administrator rights Deguffer
/// has not got, why its program cannot be uninstalled, and the size its installer recorded.
/// </summary>
/// <param name="Standing">The standing badge's word.</param>
/// <param name="Reason">Why the entry is in its list, in the words the evidence supports.</param>
/// <param name="Hidden">Why Windows does not list the entry, or null where it does.</param>
/// <param name="Shield">What needs administrator rights, or null where nothing does.</param>
/// <param name="Refusal">
/// Why an installed program cannot be uninstalled, or null where it can, or where the entry is stale
/// and its only action is removal, whose one refusal is the shield.
/// </param>
/// <param name="Size">
/// The installer's estimate in bytes, or null. Never shown for a stale entry: the machine proves its
/// program gone, so the figure describes nothing on the disk.
/// </param>
public sealed record EntryMarks(string Standing, string Reason, string? Hidden, string? Shield, string? Refusal, long? Size)
{
    /// <param name="uninstall">Whether the entry's program may be uninstalled, as the page judged it.</param>
    public static EntryMarks For(InstalledEntry entry, ActionVerdict uninstall, bool isElevated) => new(
        WordFor(entry.Standing.Standing),
        entry.Standing.Reason,
        EntryListing.WhyHidden(entry.Visibility),
        ShieldFor(entry, uninstall, isElevated),
        entry.IsStale || uninstall.IsAllowed ? null : uninstall.Reason,
        entry.IsStale ? null : entry.EstimatedBytes);

    private static string WordFor(EntryStanding standing) => standing switch
    {
        EntryStanding.Stale => "Gone",
        EntryStanding.Installed => "Present",
        EntryStanding.Unproven => "Unproven",
        EntryStanding.OtherAccount => "Other account",
        _ => throw new ArgumentOutOfRangeException(nameof(standing), standing, null),
    };

    private static string? ShieldFor(InstalledEntry entry, ActionVerdict uninstall, bool isElevated)
    {
        if (entry.IsStale)
        {
            // A stale entry's only refusal is the rights it needs, so a refusal is the shield.
            return EntryRemovalPolicy.MayRemove(entry, isElevated) is { IsAllowed: false } refused ? refused.Reason : null;
        }

        // An uninstaller asks for the rights itself, so this warns of the prompt rather than refusing,
        // and only where the uninstall is offered at all.
        return uninstall.IsAllowed && entry.Key.Scope.NeedsAdministrator() && !isElevated
            ? "This program is installed for all users, so its uninstaller will usually ask for administrator rights."
            : null;
    }
}
