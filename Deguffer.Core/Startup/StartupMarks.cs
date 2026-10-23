using Deguffer.Core.Memory;

namespace Deguffer.Core.Startup;

/// <summary>
/// What an entry's row shows beside its name (§7.5): a badge for whether it starts that opens the
/// reason, a mark where its program is missing, a shield where a change needs administrator rights
/// Deguffer has not got, and what its program holds in memory now.
/// </summary>
/// <param name="State">The badge's word.</param>
/// <param name="StateReason">What the badge means for this entry, and where Deguffer read it.</param>
/// <param name="Missing">That Windows says its program is not there, or null.</param>
/// <param name="Shield">What needs administrator rights, or null where nothing does.</param>
/// <param name="Memory">
/// What its program holds now, in bytes, or null where it is not running, its program is unknown, or
/// the figure is off. <see cref="MemoryNote"/> says which.
/// </param>
/// <param name="IsRunning">
/// Whether its program runs now, or null where nothing could say: the program is unknown, or the
/// processes could not be told apart from successors holding their identifiers.
/// </param>
/// <param name="MemoryNote">The memory figure's sentence: what it is, or why there is none.</param>
public sealed record StartupMarks(
    string State,
    string StateReason,
    string? Missing,
    string? Shield,
    long? Memory,
    bool? IsRunning,
    string MemoryNote)
{
    public static StartupMarks For(StartupEntry entry, RunningProgram? running, ProcessFigures figures, bool isElevated)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new StartupMarks(
            WordFor(entry.State),
            ReasonFor(entry),
            entry.Standing is ProgramStanding.Missing
                ? $"Windows says its program, {entry.Program}, is not there, so nothing starts from this entry."
                : null,
            ShieldFor(entry, isElevated),
            running is { IsRunning: true, PrivateWorkingSet: { } bytes } ? bytes : null,
            entry.Program is null || figures is not ProcessFigures.Checked ? null : running?.IsRunning,
            MemoryNoteFor(entry, running, figures));
    }

    private static string WordFor(StartupState state) => state switch
    {
        StartupState.Starts => "Starts",
        StartupState.Disabled => "Disabled",
        StartupState.Unknown => "Unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static string ReasonFor(StartupEntry entry)
    {
        if (entry.IsPackaged)
        {
            return entry.State switch
            {
                StartupState.Starts => "Windows starts this app's startup task at sign-in, as this account's own record of it says.",
                StartupState.Disabled => "This app's startup task is disabled for this account, by you, by the app or by a policy.",
                _ => "Windows has no record Deguffer can read of whether this app's startup task starts.",
            };
        }

        return entry.State switch
        {
            StartupState.Starts when entry.Approval?.Value is null =>
                "Windows starts this entry at sign-in: nothing records that it should not.",
            StartupState.Starts => "Windows starts this entry at sign-in, as its record beside the entry says.",
            StartupState.Disabled when entry.DisabledAt is { } when =>
                $"Disabled on {when.ToLocalTime():d}. The entry is still there, and Windows skips it at sign-in.",
            StartupState.Disabled => "Disabled. The entry is still there, and Windows skips it at sign-in.",
            _ when entry.Approval?.Value is null =>
                "Windows would not open its record of whether this entry starts.",
            _ => "Windows recorded whether this entry starts in a form Deguffer has not seen, so it does not guess.",
        };
    }

    /// <summary>A shield where a change would be offered but for the rights it needs.</summary>
    private static string? ShieldFor(StartupEntry entry, bool isElevated) =>
        !isElevated && !entry.IsPackaged && entry.Key.Source.NeedsAdministrator() && entry.Approval is { IsReplaceable: true }
            ? "This entry starts for every user, so changing it needs administrator rights."
            : null;

    private static string MemoryNoteFor(StartupEntry entry, RunningProgram? running, ProcessFigures figures)
    {
        if (entry.Program is null)
        {
            return "Deguffer cannot tell which program this entry starts, so it cannot say what that program holds now.";
        }

        if (figures is not ProcessFigures.Checked)
        {
            return "Deguffer could not check this machine's per-process figures, so it does not say what any program holds now.";
        }

        return running switch
        {
            null => "Deguffer did not read what this program holds now.",
            { IsRunning: false } => "Not running now.",
            { Processes: 1 } => "Its private working set now: memory no other process can use. A lower bound.",
            { Processes: var count } => $"The private working set of the {count} processes running it now: memory no other process can use. A lower bound.",
        };
    }
}
