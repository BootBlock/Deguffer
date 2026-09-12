using Deguffer.Core.Scanning;

namespace Deguffer.Core.Memory;

/// <summary>
/// What a memory picture has to say about itself: that its figures are lower bounds, and every way
/// this read fell short of the whole truth (§7.2).
///
/// <para>One sentence per thing that is missing, so the view states what it could not measure rather
/// than drawing a smaller number and letting the reader take it for the whole. Nothing here says what
/// to do about anything.</para>
/// </summary>
public static class MemoryNotes
{
    /// <summary>
    /// The sentences to show beside <paramref name="tree"/>. The first is always there; the rest are
    /// whatever this read could not do.
    /// </summary>
    public static IReadOnlyList<string> For(MemoryTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        var snapshot = tree.Snapshot;

        var notes = new List<string>
        {
            "Every figure here is a lower bound. What a program holds beyond its own pages in memory, "
            + "and the memory Windows cannot separate unelevated, are not in it.",
        };

        if (WhyNoProcesses(snapshot.Processes.Figures) is { } figures)
        {
            notes.Add(figures);
        }
        else if (!snapshot.Processes.Complete)
        {
            notes.Add(
                "The process table could not be read to its end, so processes after that point are "
                + "missing and the parts holding them are short.");
        }

        notes.Add(snapshot.Services.Listing switch
        {
            ServiceListing.NotListed =>
                "Windows would not list its services for this account, so every host is drawn as an "
                + "ordinary process.",
            ServiceListing.ListedInPart =>
                "The service list could not be read to its end, so some hosts are drawn as ordinary "
                + "processes.",
            _ =>
                "Windows leaves out the services this account may not query, without saying so, so "
                + "some hosts are drawn as ordinary processes.",
        });

        if (snapshot.System.ListState != MemoryListState.Checked)
        {
            notes.Add(
                "The free, modified and standby lists could not be used, so the system cache is drawn "
                + "in their place and holds all three.");
        }

        if (tree.Overcount > 0)
        {
            notes.Add(
                $"The figures were read a moment apart and overlap by {FreeSpace.Format(tree.Overcount)}, "
                + "so the parts add up to more than physical memory and nothing is left unattributed.");
        }

        return notes;
    }

    /// <summary>
    /// Why no process is drawn, or null where they are. Each answer names what could not be checked,
    /// because a picture with no process in it is otherwise a picture of a machine running nothing.
    /// </summary>
    private static string? WhyNoProcesses(ProcessFigures figures) => figures switch
    {
        ProcessFigures.Checked => null,

        ProcessFigures.NotOnThisArchitecture =>
            "No process is drawn: a 32-bit Deguffer cannot read these figures on 64-bit Windows.",

        ProcessFigures.NothingToCheckAgainst =>
            "No process is drawn: this Windows offers no documented figure to check the per-process "
            + "ones against.",

        _ =>
            "No process is drawn: the process table did not agree with what Windows documents about "
            + "Deguffer's own process, so its figures are not used.",
    };
}
