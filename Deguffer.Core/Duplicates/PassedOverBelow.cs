namespace Deguffer.Core.Duplicates;

/// <summary>
/// What a search passes over below one searched location (§7.4): the places reference locations name
/// that could not be resolved, and, unless the search includes them, what Explore refuses at and
/// below and the folders programs are installed in.
///
/// <para><b>One answer for two askers.</b> The walk that lists the location asks it of each folder
/// before listing it, so a place passed over is never listed, and <see cref="CandidateWalk"/> asks it
/// of each entry of the tree, so the place is named. Asked through one member, the two cannot come to
/// disagree: a place the walk skipped and the search did not name would read as a place that held
/// nothing.</para>
/// </summary>
/// <param name="below">What is passed over by default below the location, or null where the search includes it.</param>
internal sealed class PassedOverBelow(UnresolvedReferences unresolved, PassedOverPlaces.Below? below)
{
    /// <summary>
    /// Why the entry <paramref name="child"/>, named <paramref name="childName"/>, of the searched
    /// folder <paramref name="parent"/> is passed over with everything in it, or null where it is
    /// searched as its folder was.
    /// </summary>
    /// <param name="parent">The folder holding it, in display form.</param>
    /// <param name="parentIsRoot">Whether <paramref name="parent"/> is the location itself.</param>
    /// <param name="child">The entry's own path, in display form.</param>
    public string? WhyPassedOver(string parent, bool parentIsRoot, string child, string childName) =>
        unresolved.WhyPassedOver(child) ?? below?.WhyPassedOver(parent, parentIsRoot, child, childName);
}
