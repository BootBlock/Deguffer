using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// The structural table as <see cref="ExploreActionPolicy"/> asks it: each region resolved, followed
/// to every path it is reachable at, and the innermost one holding a path found.
///
/// <para><b>Innermost by levels, not by the length of a region's path.</b> The two agree only where
/// every region is named the way the path asked about is. With <c>T:</c> substituted for the
/// temporary folder, the profile's permission at <c>C:\Users\testuser</c> is longer than the
/// temporary folder's refusal at <c>T:\</c>, and read by length it answered for the temporary
/// folder.</para>
///
/// <para><b>Asked of one path at a time.</b> The policy asks about every path the item is reachable
/// at and refuses where any answer refuses. Two mounts of one volume can put the item inside the
/// profile at one path and inside another account's profile at another, and the innermost region of
/// the first must not answer for the second.</para>
/// </summary>
internal sealed class RegionTable
{
    private readonly IReadOnlyList<(ProtectedRegion Region, ReachedFolder Folder)> _regions;

    /// <param name="regions">
    /// The table as the caller gave it. A path-only entry is put first among entries for one folder,
    /// because it is the exception to the entry for that folder and below, and an unordered table
    /// would resolve that by declaration order instead.
    /// </param>
    /// <param name="volumes">Asked every other path each region is reachable at, once.</param>
    public RegionTable(IEnumerable<ProtectedRegion> regions, IVolumeInventory volumes)
    {
        // A region whose path will not resolve is dropped, not kept with the value it arrived
        // with. An empty one is the case that matters: LongPath.Contains("", candidate) builds the
        // prefix "\\" and so matches every UNC path, which would refuse a whole network share with a
        // sentence naming no directory at all. A path that names nothing protects nothing, and
        // %ProgramFiles(x86)% is genuinely empty on a 32-bit Windows.
        _regions =
        [
            .. regions
                .Select(r => (Region: r, Path: LongPath.Configured(r.Path)))
                .Where(r => r.Path is not null)
                .Select(r => r.Region with { Path = r.Path! })
                .OrderBy(r => r.Scope == RegionScope.PathOnly ? 0 : 1)
                .Select(r => (r, ReachedFolder.At(r.Path, volumes))),
        ];
    }

    /// <summary>
    /// Every refusing region, whichever scope it has, with the folder it names: a folder holding the
    /// profile or <c>C:\Windows</c> takes it along as surely as one holding a tool's folder does.
    /// </summary>
    public IEnumerable<(string Reason, ReachedFolder Folder)> Refusing =>
        _regions
            .Where(r => !r.Region.Verdict.IsAllowed)
            .Select(r => (r.Region.Verdict.Reason, r.Folder));

    /// <summary>
    /// The innermost region covering <paramref name="place"/>, or null where none does: a path-only
    /// entry where it names that folder itself, and otherwise the entry for the nearest folder holding
    /// it.
    /// </summary>
    /// <param name="place">One path the item is reachable at, in <see cref="ReachedFolder.Comparable"/> form.</param>
    public ProtectedRegion? Innermost(string place)
    {
        ProtectedRegion? innermost = null;
        int? levels = null;

        foreach (var (region, folder) in _regions)
        {
            // Strictly deeper only, so of two entries for one folder the path-only one, ordered
            // first, answers for the folder itself.
            if (folder.LevelsTo(place) is not { } below
                || (region.Scope == RegionScope.PathOnly && below > 0)
                || below >= levels)
            {
                continue;
            }

            innermost = region;
            levels = below;
        }

        return innermost;
    }
}
