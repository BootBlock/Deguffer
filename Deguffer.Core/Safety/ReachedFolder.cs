namespace Deguffer.Core.Safety;

/// <summary>
/// A folder as every path it is reachable at, each made comparable with any other, so that a check of
/// whether one folder holds another is asked of the folders rather than of how they were named.
///
/// <para><b>A comparison of text is right only where both paths reach the folder the same way.</b>
/// With the system volume also mounted at <c>D:\SysMount\</c>, or <c>S:</c> substituted for the
/// profile, <c>S:\AppData</c> is inside the profile and its text names nothing there. Two settings
/// naming one folder through different letters read as two folders, and a folder nested in another
/// through an alias reads as beside it. <see cref="VolumeRoot.Places"/> gives every path a folder is
/// at, and one folder holds another where any of its paths holds any of the other's.</para>
///
/// <para><b>Both sides are expanded</b>, because both may be a setting. A temporary folder named
/// through one letter and a tool's folder named through another reach a common path only once each is
/// followed to where it is.</para>
/// </summary>
public sealed class ReachedFolder
{
    private ReachedFolder(IReadOnlyList<string> places, bool isVolumeTop)
    {
        Places = places;
        IsVolumeTop = isVolumeTop;
    }

    /// <summary>
    /// Every path the folder is reachable at, comparable with <see cref="Comparable"/>: the path it was
    /// asked about first.
    /// </summary>
    public IReadOnlyList<string> Places { get; }

    /// <summary>
    /// Whether the folder is the top of a drive, a share or a volume wherever it is reached, where
    /// <see cref="Places"/> holds only the path asked about.
    /// </summary>
    public bool IsVolumeTop { get; }

    /// <summary>The folder at <paramref name="path"/>, asked of <paramref name="volumes"/> where else it is.</summary>
    /// <param name="path">A full path, in either form <see cref="LongPath"/> produces.</param>
    public static ReachedFolder At(string path, IVolumeInventory volumes)
    {
        var comparable = Comparable(path);

        // The first place is the path itself, which is comparable already.
        return VolumeRoot.Places(volumes, comparable) is { } places
            ? new ReachedFolder([comparable, .. places.Skip(1).Select(place => Comparable(place.Path))], false)
            : new ReachedFolder([comparable], true);
    }

    /// <summary>Whether this folder is <paramref name="inner"/> or holds it, at any path either is reachable at.</summary>
    public bool Holds(ReachedFolder inner) =>
        Places.Any(outer => inner.Places.Any(place => LongPath.Contains(outer, place)));

    /// <summary>Whether this folder and <paramref name="other"/> are one folder reached two ways.</summary>
    public bool IsSameAs(ReachedFolder other) =>
        Places.Any(place => other.Places.Contains(place, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Without an 8.3 alias, the extended-length prefix or a trailing separator, so a path from a step
    /// and one from the environment compare as the same folder. A root keeps its separator.
    ///
    /// <para>Each place is made comparable as the path is. A letter <c>subst</c> made for a folder named
    /// in its short form leads to that short form, which would match nothing.</para>
    /// </summary>
    public static string Comparable(string path) =>
        Path.TrimEndingDirectorySeparator(LongPath.Display(LongPath.Unaliased(path)));
}
