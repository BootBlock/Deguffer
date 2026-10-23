namespace Deguffer.Core.Startup;

/// <summary>The one change §7.5 makes, in whichever direction the entry needs.</summary>
public enum StartupChange
{
    /// <summary>Record that the entry should not start at sign-in.</summary>
    Disable,

    /// <summary>Record that it should start again.</summary>
    Enable,
}

/// <summary>Whether an entry may be changed, which way, and the sentence that says so.</summary>
/// <param name="Change">The change that would be made, or null where none would.</param>
public sealed record StartupVerdict(StartupChange? Change, string Reason)
{
    public bool IsAllowed => Change is not null;
}

/// <summary>
/// What may be changed (§7.5), decided when an entry is selected and again by
/// <see cref="StartupSwitcher"/> immediately before it writes. Where more than one refusal applies,
/// the first in the order below is the one given, so the same entry always gives the same answer.
/// </summary>
public static class StartupChangePolicy
{
    public static StartupVerdict MayChange(StartupEntry entry, bool isElevated)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.IsPackaged || entry.Approval is not { } approval)
        {
            return Refuse("Windows lets only the app itself change its startup task, and you through Settings or Task "
                + "Manager. Open Startup apps in Settings to change it.");
        }

        if (!approval.IsReplaceable)
        {
            return Refuse(approval.Value is null
                ? "Windows would not open its record of whether this entry starts, so Deguffer will not change it."
                : "Windows recorded whether this entry starts in a form Deguffer has not seen, so Deguffer reports it "
                    + "and changes nothing. Task Manager or Settings can still change it.");
        }

        if (entry.Key.Source.NeedsAdministrator() && !isElevated)
        {
            return Refuse("This entry starts for every user, so changing it needs administrator rights. Reopen Deguffer "
                + "as administrator to change it.");
        }

        return entry.State switch
        {
            StartupState.Starts => new StartupVerdict(StartupChange.Disable,
                "Disable records that this entry should not start at sign-in, as Task Manager does. The entry itself "
                + "stays as it is, and Enable lets it start again."),
            StartupState.Disabled => new StartupVerdict(StartupChange.Enable,
                "Enable records that this entry should start at sign-in again, as Task Manager does."),
            _ => Refuse("Whether this entry starts is unknown, so Deguffer will not change it."),
        };
    }

    private static StartupVerdict Refuse(string reason) => new(null, reason);
}
