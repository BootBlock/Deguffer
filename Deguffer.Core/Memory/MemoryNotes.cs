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
            // What the reader is looking at, said where they are looking at it. Applications and
            // Services are still drawn, because the tree has three parts whatever could be measured,
            // and a part standing at nothing with no reason beside it reads as a machine running
            // nothing rather than as a figure that was turned off.
            notes.Add(
                figures
                + " Applications and Services show nothing as a result: what those processes hold is "
                + "inside the part no figure attributes.");
        }
        else if (!snapshot.Processes.Complete)
        {
            notes.Add(
                "The process table could not be read to its end, so processes after that point are "
                + "missing and the parts holding them are short.");
        }

        notes.Add(WhichHostsAreMissing(snapshot.Services.Listing));

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
    /// Which service hosts this read cannot name, in the words the picture uses (§7.2).
    ///
    /// <para>Public because §7.2.1's confirmation says the same thing in the same words. The close
    /// refuses a process hosting a service, and that row is the one rule in the table that cannot be
    /// complete: a host whose services this account may not query is drawn as an ordinary process,
    /// and the rule does not reach it. A dialog that said so in words of its own would be a second
    /// statement of one fact, and the two would drift.</para>
    ///
    /// <para>Every listing state has a sentence, <see cref="ServiceListing.Listed"/> included: the
    /// list Windows returns in full still leaves out what this account may not query, and says
    /// nothing about having done so.</para>
    /// </summary>
    public static string WhichHostsAreMissing(ServiceListing listing) => listing switch
    {
        ServiceListing.NotListed =>
            "Windows would not list its services for this account, so every host is drawn as an "
            + "ordinary process.",
        ServiceListing.ListedInPart =>
            "The service list could not be read to its end, so some hosts are drawn as ordinary "
            + "processes.",
        ServiceListing.Listed =>
            "Windows leaves out the services this account may not query, without saying so, so "
            + "some hosts are drawn as ordinary processes.",

        _ => throw new ArgumentOutOfRangeException(nameof(listing)),
    };

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
