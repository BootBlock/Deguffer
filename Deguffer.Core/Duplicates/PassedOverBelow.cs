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
///
/// <para><b>Asked once a folder, then once a name.</b> A drive holds millions of files, and whether a
/// rule can apply among a folder's children depends almost wholly on the folder, so that part is
/// worked out once for each folder, and a child's path is built only where a rule may apply to it.</para>
/// </summary>
/// <param name="below">What is passed over by default below the location, or null where the search includes it.</param>
internal sealed class PassedOverBelow(UnresolvedReferences unresolved, PassedOverPlaces.Below? below)
{
    /// <summary>What is passed over among the children of <paramref name="parent"/>, a searched folder.</summary>
    /// <param name="parent">The folder, in display form.</param>
    /// <param name="parentIsRoot">Whether <paramref name="parent"/> is the location itself.</param>
    public InFolder In(string parent, bool parentIsRoot) =>
        new(parent, unresolved.AnyAtOrInside(parent) ? unresolved : null, below?.In(parent, parentIsRoot));

    /// <summary>What is passed over among the children of one searched folder.</summary>
    internal readonly struct InFolder
    {
        private readonly string _parent;
        private readonly UnresolvedReferences? _unresolved;
        private readonly PassedOverPlaces.InFolder? _below;

        /// <param name="unresolved">The unresolved references, or null where none is in the folder.</param>
        internal InFolder(string parent, UnresolvedReferences? unresolved, PassedOverPlaces.InFolder? below)
        {
            _parent = parent;
            _unresolved = unresolved;
            _below = below;
        }

        /// <summary>
        /// Whether the child named <paramref name="childName"/> may be passed over, so is to be asked
        /// <see cref="WhyPassedOver"/>. Where not, it is searched as its folder was.
        /// </summary>
        public bool MayPassOver(string childName) =>
            _unresolved is not null || _below?.MayPassOver(childName) == true;

        /// <summary>
        /// Why the entry <paramref name="child"/>, named <paramref name="childName"/>, is passed over
        /// with everything in it, or null where it is searched as its folder was.
        /// </summary>
        /// <param name="child">The entry's own path, in display form.</param>
        public string? WhyPassedOver(string child, string childName) =>
            _unresolved?.WhyPassedOver(child) ?? _below?.WhyPassedOver(child, childName);

        /// <summary>Whether the child named <paramref name="childName"/> is passed over, building its path only where that is needed.</summary>
        public bool PassesOver(string childName) =>
            MayPassOver(childName) && WhyPassedOver(Path.Join(_parent, childName), childName) is not null;
    }
}
