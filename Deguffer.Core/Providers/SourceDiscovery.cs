using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>Candidate directories, and whether any root had to be walked.</summary>
/// <param name="Candidates">Every directory of a sought name inside an approved root.</param>
/// <param name="FellBack">
/// True if any root had to be walked because the volume index could not answer for it. §5.5
/// requires the fallback to be observable, and a discovery pass that took thirty seconds is
/// otherwise indistinguishable from a large source tree. False for a root whose walk finished while
/// the index was still being read, because nothing was unavailable there: the walk was quicker.
/// </param>
/// <param name="UnreadableDirectories">
/// Directories inside an approved root that refused to be listed, so the walk never went below
/// them. Reported rather than dropped: part of a root the user approved went unsearched, and a plan
/// that says nothing about it describes a search it did not perform. Always empty for a root the
/// index answered, because reading the volume table enumerates nothing.
/// </param>
/// <param name="RefusedRoots">
/// Approved roots no pass looked inside at all, because each is on a volume whose contents are not
/// on this machine and the user has not said to search one anyway. Reported for the reason
/// <paramref name="UnreadableDirectories"/> is, and with more force: this is Deguffer's own decision
/// rather than Windows', and the whole of a folder the user chose went unsearched.
/// </param>
public sealed record SourceDiscovery(
    IReadOnlyList<string> Candidates,
    bool FellBack,
    IReadOnlyList<string> UnreadableDirectories,
    IReadOnlyList<string> RefusedRoots)
{
    /// <summary>
    /// Approved roots Windows would not describe, so no part of them was searched and none of them
    /// is known to be there.
    ///
    /// <para>Apart from <see cref="UnreadableDirectories"/>, which holds directories that were
    /// reached and would not be listed: that sentence asserts the directory exists, and nothing
    /// here established it. Apart from <see cref="RefusedRoots"/> too, which is Deguffer's own
    /// decision rather than Windows'.</para>
    /// </summary>
    public IReadOnlyList<string> UnreachedRoots { get; init; } = [];

    /// <summary>
    /// Approved roots not looked in because nothing the discovery's scope covers is in one, holds one
    /// or is one (<see cref="ProtectionScope"/>). Always empty for a search of everything, as a plan's
    /// is.
    /// </summary>
    public IReadOnlyList<string> NotSearchedRoots { get; init; } = [];

    /// <summary>The candidates called any of <paramref name="names"/>, for the provider that owns them.</summary>
    /// <remarks>
    /// Every source-tree row asks this of every candidate in each pass, so a candidate's name is
    /// compared where it sits in the path rather than cut out of it.
    /// </remarks>
    public IReadOnlyList<string> Named(IReadOnlyList<string> names)
    {
        var named = new List<string>();
        for (var i = 0; i < Candidates.Count; i++)
        {
            if (IsCalledAnyOf(Candidates[i], names))
            {
                named.Add(Candidates[i]);
            }
        }

        return named;
    }

    private static bool IsCalledAnyOf(string candidate, IReadOnlyList<string> names)
    {
        var name = Path.GetFileName(candidate.AsSpan().TrimEnd(Path.DirectorySeparatorChar));
        for (var i = 0; i < names.Count; i++)
        {
            if (name.Equals(names[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
