namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// A catalogue entry and the path it was found at, for a caller that asked about something further
/// down.
///
/// <para>The map needs it and a list does not, and the difference is geometry rather than taste. A
/// treemap gives a directory a one-pixel frame around its children — see
/// <see cref="Layout.TreemapLayout"/> — so the pointer is nearly always on a file several levels
/// below the folder that explains it, and a lookup that only answered about the exact path answered
/// nothing over the whole of <c>C:\Windows</c>. A list row is the folder itself, so it asks
/// <see cref="ItemGuide.Describe"/> and gets its own answer or none.</para>
///
/// <para>Where the answer came from above, the text says so before it says anything else. Otherwise
/// an explanation of what <c>WinSxS</c> is reads as an explanation of the file the pointer is
/// actually on.</para>
/// </summary>
/// <param name="Item">What Deguffer knows.</param>
/// <param name="Path">Where it knows it about, which is the asked-for path or an ancestor of it.</param>
/// <param name="IsExact">
/// Whether <paramref name="Path"/> is the path that was asked about. Carried rather than compared
/// by the caller: the lookup normalises what it was given, so the two can differ in case and in
/// spelling while naming one directory.
/// </param>
public sealed record KnownMatch(KnownItem Item, string Path, bool IsExact)
{
    /// <summary>
    /// The whole of what a hovering reader is shown.
    ///
    /// <para>No counterpart to <see cref="KnownItem.Tip"/>'s <c>facts</c>, because the one caller
    /// this has is the map, and the map already puts the size and the date on the status line under
    /// the picture rather than inside the popup.</para>
    /// </summary>
    public string Tip() => IsExact ? Item.Tip() : $"Inside {Path}{KnownItem.Blank}{Item.Tip()}";
}
