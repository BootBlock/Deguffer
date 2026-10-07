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
    /// Whether the path asked about is the top of a drive, a share or a volume, which is never removed
    /// however else it is reached. Its other <see cref="Places"/> are the other places its volume is
    /// mounted, and the folder its letter stands for where <c>subst</c> made the letter.
    /// </summary>
    public bool IsVolumeTop { get; }

    /// <summary>The folder at <paramref name="path"/>, asked of <paramref name="volumes"/> where else it is.</summary>
    /// <param name="path">A full path, in either form <see cref="LongPath"/> produces.</param>
    public static ReachedFolder At(string path, IVolumeInventory volumes)
    {
        var comparable = Comparable(path);

        if (VolumeRoot.Places(volumes, comparable) is { } places)
        {
            return Following(comparable, places);
        }

        List<string> tops = [comparable];
        AddTopPlaces(tops, comparable, volumes);

        return new ReachedFolder(tops, true);
    }

    /// <summary>
    /// The folder at <paramref name="path"/>, from the <paramref name="places"/>
    /// <see cref="VolumeRoot.Places"/> gave for it, for a caller that read them for rules of its own
    /// and would otherwise ask the machine the same questions twice.
    /// </summary>
    internal static ReachedFolder Following(string path, IReadOnlyList<VolumePlace> places) =>
        new([Comparable(path), .. places.Skip(1).Select(place => Comparable(place.Path))], false);

    /// <summary>Whether this folder is <paramref name="inner"/> or holds it, at any path either is reachable at.</summary>
    public bool Holds(ReachedFolder inner) => PathTo(inner) is not null;

    /// <summary>
    /// Where <paramref name="inner"/> lies below this folder, as a path relative to it, at the first
    /// pair of their places where one holds the other: <c>.</c> where the two are one folder, and null
    /// where this folder does not hold it at any of them.
    ///
    /// <para>For a caller that has to name <paramref name="inner"/> the way it names this folder. A
    /// program working at <c>S:\app\src</c>, with <c>S:</c> substituted for <c>C:\Source</c>, is at
    /// <c>app\src</c> below <c>C:\Source</c>, which its own text does not say.</para>
    /// </summary>
    public string? PathTo(ReachedFolder inner)
    {
        foreach (var outer in Places)
        {
            foreach (var place in inner.Places)
            {
                if (LongPath.Contains(outer, place))
                {
                    return Path.GetRelativePath(outer, place);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// How many folders below this one <paramref name="inner"/> is: none where the two are one
    /// folder, and null where this folder does not hold it.
    ///
    /// <para>For a caller choosing the innermost of several folders holding one. Their paths' lengths
    /// say that only where every one is named the same way, and <c>S:\vcpkg</c> is inside
    /// <c>C:\Users\testuser\src</c> where <c>S:</c> stands for the second.</para>
    /// </summary>
    public int? LevelsTo(ReachedFolder inner) => PathTo(inner) switch
    {
        null => null,
        "." => 0,
        var relative => relative.Split(Path.DirectorySeparatorChar).Length,
    };

    /// <summary>
    /// <paramref name="inner"/> named below this folder the way <paramref name="named"/> names this
    /// folder, or null where this folder does not hold it at any path either is reachable at.
    ///
    /// <para>For a check whose next rule is asked of text: the boundary of a search, a recogniser, or
    /// a list of projects a solution names, each written in the form the root was configured in.</para>
    /// </summary>
    /// <param name="named">How this folder is named, which may be a form <see cref="Places"/> does not use.</param>
    public string? Naming(ReachedFolder inner, string named) => PathTo(inner) switch
    {
        null => null,
        "." => named,
        var relative => Path.Combine(named, relative),
    };

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

    /// <summary>
    /// Adds to <paramref name="places"/> every other place the top of a volume or a letter,
    /// <paramref name="top"/>, is reachable at: the other places its volume is mounted, and the folder
    /// <c>subst</c> made its letter stand for, at every path that folder is reachable at in turn.
    ///
    /// <para><b>A program working at <c>S:\</c> is working in the folder the letter stands for.</b>
    /// <see cref="VolumeRoot.Places"/> answers nothing for a top, because a top is never removed, and
    /// read as itself alone the program was not using a folder below the one <c>S:</c> stands
    /// for.</para>
    ///
    /// <para>A folder is followed only the first time it is found, because two letters may stand for
    /// each other.</para>
    /// </summary>
    private static void AddTopPlaces(List<string> places, string top, IVolumeInventory volumes)
    {
        foreach (var mountPoint in volumes.MountPointsOf(top))
        {
            Add(places, Comparable(mountPoint));
        }

        if (!string.Equals(Path.GetPathRoot(top), top, StringComparison.OrdinalIgnoreCase)
            || volumes.SubstituteOf(top) is not { } substitute
            || !Add(places, Comparable(substitute)))
        {
            return;
        }

        var folder = places[^1];

        if (VolumeRoot.Places(volumes, folder) is { } below)
        {
            foreach (var place in below.Skip(1))
            {
                Add(places, Comparable(place.Path));
            }
        }
        else
        {
            AddTopPlaces(places, folder, volumes);
        }
    }

    /// <summary>Adds <paramref name="place"/> where it is not there already, and says whether it was not.</summary>
    private static bool Add(List<string> places, string place)
    {
        if (places.Contains(place, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        places.Add(place);

        return true;
    }
}
