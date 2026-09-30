namespace Deguffer.Core.InstalledApps;

/// <summary>Whether an action may go ahead on an entry, and why not when it may not.</summary>
/// <param name="Reason">The sentence shown on the row. A refusal is a sentence, never a disabled button (§7.1).</param>
public sealed record ActionVerdict(bool IsAllowed, string Reason)
{
    public static ActionVerdict Allow(string reason) => new(IsAllowed: true, reason);

    public static ActionVerdict Refuse(string reason) => new(IsAllowed: false, reason);
}

/// <summary>
/// What may be removed (§7.3), decided when an entry is selected and again by
/// <see cref="EntryRemover"/> immediately before deleting.
/// </summary>
public static class EntryRemovalPolicy
{
    public static ActionVerdict MayRemove(InstalledEntry entry, bool isElevated)
    {
        if (!entry.IsStale)
        {
            return ActionVerdict.Refuse(
                $"Deguffer removes only an entry the machine proves is stale. {entry.Standing.Reason}");
        }

        if (entry.Key.Scope.NeedsAdministrator() && !isElevated)
        {
            return ActionVerdict.Refuse(
                "This entry is for all users, and removing it needs administrator rights. Reopen Deguffer as administrator to remove it.");
        }

        return ActionVerdict.Allow(entry.Standing.Reason);
    }
}
