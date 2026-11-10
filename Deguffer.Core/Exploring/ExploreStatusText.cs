using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;

namespace Deguffer.Core.Exploring;

/// <summary>
/// What the Explore page's status line says of where the page is pointed, and when a refusal it
/// stated is taken back.
/// </summary>
public static class ExploreStatusText
{
    /// <summary>
    /// What the line says before anything has been measured, and what it goes back to when a refusal
    /// is taken off it. See <see cref="Retargeted"/>.
    /// </summary>
    public const string Prompt = "Choose a drive or a folder and scan it to see what is using the space.";

    /// <summary>
    /// What the line says when the folder picker returned something that is not a folder on a disk.
    /// The target stays where it was, so a refusal of it still stands and is said again: without it
    /// the Scan button stays greyed out with nothing on the page saying why.
    /// </summary>
    /// <param name="standing">Why the page refuses the target it keeps, or null where it does not.</param>
    public static string NotOnDisk(string? standing) =>
        standing is null ? PickedFolder.NotOnDisk : $"{PickedFolder.NotOnDisk} {standing}";

    /// <summary>
    /// What the line says once a scan of <paramref name="tree"/> has finished: nothing, because the
    /// map, its readout and its rows state the figures; or, where the scan could not list the folder
    /// it was pointed at, that nothing was measured.
    ///
    /// <para>Without the second, a share whose computer did not answer, or a mapped drive whose
    /// connection is gone, finishes as an empty map and a cleared line, which reads as a drive that
    /// holds nothing. The walk cannot tell which of the causes it met, so all of them are named.</para>
    /// </summary>
    public static StatusLine Finished(ExploreTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return tree.ListingWasRefused(tree.RootNode)
            ? StatusLine.Said(
                $"Windows would not list '{tree.PathOf(tree.RootNode)}', so nothing in it was measured. It may "
                + "no longer be there, the computer sharing it may not have answered, or this account may "
                + "not open it.")
            : StatusLine.Empty;
    }

    /// <summary>
    /// What the line says once the page is pointed somewhere new: why the page will not scan it, or
    /// the prompt where the line still holds a refusal the page stated and nothing refuses now, or
    /// whatever it said before.
    ///
    /// <para>§7.1 asks a refusal to say what it is, and a greyed-out Scan button says nothing at all.
    /// The retraction is the other half of that: the line describes what pressing Scan would do, so
    /// leaving "Deguffer does not scan this drive" up while the button is live and aimed at an
    /// ordinary volume states the opposite of the truth.</para>
    ///
    /// <para>It takes back its own sentence and nobody else's. A scan's result and a selection's
    /// report write this same line, and neither is made untrue by a change of target, so clearing
    /// unconditionally would throw away whichever of them was there.</para>
    ///
    /// <para>Which sentence is the page's refusal is said by where it came from
    /// (<see cref="StatusOrigin.Refusal"/>), never by comparing its words with what was stated.</para>
    /// </summary>
    /// <param name="shown">What the line holds now.</param>
    /// <param name="refusal">Why the page will not scan where it now points, or null where it will.</param>
    public static StatusLine Retargeted(StatusLine shown, string? refusal)
    {
        ArgumentNullException.ThrowIfNull(shown);

        return refusal is not null ? StatusLine.Refused(refusal)
            : shown.Origin == StatusOrigin.Refusal ? StatusLine.Said(Prompt)
            : shown;
    }
}
