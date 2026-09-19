using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// The catalogue, resolved against this machine: given a path, what Deguffer knows about it.
///
/// <para>Separate from <see cref="KnownItems"/> because they are different jobs (G1). That one is
/// the text, and it names places rather than paths so it stays true of every machine. This one
/// turns those places into the addresses this machine actually uses, and answers the lookup.</para>
///
/// <para>It decides nothing about deletion. <see cref="Acting.ExploreActionPolicy"/> is what stands
/// between a size picture and <c>C:\Windows</c>, and a reference the reader is shown must never
/// become a second, weaker copy of that rule. This type is read on hover and its answer reaches
/// nothing that removes anything.</para>
/// </summary>
public sealed class ItemGuide
{
    /// <summary>
    /// Every anchored entry, keyed by the address it resolved to on this machine.
    ///
    /// <para>Resolved once, at construction, rather than per lookup. The page asks about every row
    /// of every directory it opens and again on every shape the pointer settles on, so a pass over
    /// the whole catalogue building paths would be that work repeated for each of them (G4).</para>
    /// </summary>
    private readonly Dictionary<string, KnownItem> _byPath;

    /// <summary>
    /// The <see cref="KnownPlace.VolumeRoot"/> entries, keyed by where they sit below the root
    /// rather than resolved into paths — they are true of every volume, and the set of volumes
    /// changes while the app is open.
    /// </summary>
    private readonly Dictionary<string, KnownItem> _belowVolumeRoot;

    /// <summary>
    /// How many segments deep each <see cref="_belowVolumeRoot"/> key is, so a lookup can find the
    /// one key a path could match from its own trailing segments before it asks the machine where
    /// the path's volume is mounted.
    ///
    /// <para>That question costs a call into Windows, and the page asks about every row it draws and
    /// every shape the pointer settles on. Nearly none of them is named like anything Windows keeps
    /// at a volume root, so asking only for the ones that are keeps the lookup a dictionary probe
    /// (G4) without remembering an answer that a volume mounted a moment later would make wrong.</para>
    /// </summary>
    private readonly SortedSet<int> _belowVolumeRootDepths = [];

    private readonly IVolumeInventory _volumes;

    /// <summary><see cref="KnownPlace.Anywhere"/>, keyed by name.</summary>
    private readonly Dictionary<string, KnownItem> _byName;

    /// <summary><see cref="KnownPlace.AnywhereByExtension"/>, keyed by extension with its dot.</summary>
    private readonly Dictionary<string, KnownItem> _byExtension;

    /// <param name="entries">The catalogue. Ordinarily <see cref="KnownItems.All"/>.</param>
    /// <param name="anchors">
    /// Where each place is on this machine. A place missing from here, or present with a value that
    /// names no directory, contributes nothing — which is the right answer for
    /// <c>%ProgramFiles(x86)%</c> on a 32-bit Windows, where it is empty and there is nothing there
    /// to explain.
    /// </param>
    /// <param name="volumes">
    /// Asked where a path's volume is mounted, so what Windows keeps at the top of a volume mounted
    /// at a folder is explained as it is at the top of a drive, by the rule
    /// <see cref="Acting.ExploreActionPolicy"/> refuses by.
    /// </param>
    public ItemGuide(
        IEnumerable<KnownItem> entries, IReadOnlyDictionary<KnownPlace, string> anchors, IVolumeInventory volumes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(volumes);

        _volumes = volumes;

        _byPath = new Dictionary<string, KnownItem>(StringComparer.OrdinalIgnoreCase);
        _belowVolumeRoot = new Dictionary<string, KnownItem>(StringComparer.OrdinalIgnoreCase);
        _byName = new Dictionary<string, KnownItem>(StringComparer.OrdinalIgnoreCase);
        _byExtension = new Dictionary<string, KnownItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            switch (entry.Place)
            {
                case KnownPlace.Anywhere:
                    _byName[entry.RelativePath] = entry;
                    break;

                case KnownPlace.AnywhereByExtension:
                    _byExtension[entry.RelativePath] = entry;
                    break;

                case KnownPlace.VolumeRoot:
                    _belowVolumeRoot[entry.RelativePath] = entry;
                    _belowVolumeRootDepths.Add(Depth(entry.RelativePath));
                    break;

                default:
                    if (Resolve(anchors, entry) is { } path)
                    {
                        _byPath[path] = entry;
                    }

                    break;
            }
        }
    }

    /// <summary>The catalogue against the machine the app is running on (G5: built once, injected).</summary>
    public static ItemGuide ForThisMachine() =>
        For(SystemDirectories.Current, UserEnvironment.Current, VolumeInventory.Current);

    /// <summary>
    /// The catalogue against the directories and volumes these seams name, which is what lets every
    /// entry be asserted against a synthetic profile rather than the developer's own.
    /// </summary>
    public static ItemGuide For(ISystemDirectories system, IUserEnvironment environment, IVolumeInventory volumes)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(environment);

        return new ItemGuide(KnownItems.All, Anchors(system, environment), volumes);
    }

    /// <summary>
    /// What Deguffer knows about <paramref name="path"/> itself, or null where it is an ordinary
    /// file or folder — which is nearly everything on a disk, and is not a failure.
    ///
    /// <para>For a caller holding the thing the entry would be about, which is what a list row is.
    /// A pointer over a picture is not, and <see cref="DescribeNearest"/> is for that.</para>
    /// </summary>
    public KnownItem? Describe(string? path) =>
        LongPath.Configured(path) is { } target ? Lookup(target) : null;

    /// <summary>
    /// What Deguffer knows about <paramref name="path"/>, or about the nearest folder above it that
    /// it does know, or null where nothing on the way to the top of the volume is described.
    ///
    /// <para>For the map, where the pointer answers with the deepest shape covering it and a folder
    /// is drawn as a one-pixel frame around its children (<see cref="Layout.TreemapLayout"/>). So
    /// the shape under the pointer is nearly always a file nobody wrote about, sitting inside a
    /// folder somebody did, and asking about the exact path left the reference unreachable
    /// everywhere except on that frame.</para>
    ///
    /// <para>The nearest described folder wins rather than the outermost, because it is the more
    /// specific claim: a file inside <c>WinSxS</c> is better explained by WinSxS than by Windows.
    /// <see cref="KnownMatch.IsExact"/> is what lets the answer say which of the two it is.</para>
    /// </summary>
    public KnownMatch? DescribeNearest(string? path)
    {
        if (LongPath.Configured(path) is not { } target)
        {
            return null;
        }

        var at = (string?)target;
        var exact = true;

        // Ends at the top of the volume: GetDirectoryName answers null for a root, which is the
        // same boundary the anchors are measured from.
        while (at is { Length: > 0 })
        {
            if (Lookup(at) is { } item)
            {
                return new KnownMatch(item, at, exact);
            }

            at = Path.GetDirectoryName(at);
            exact = false;
        }

        return null;
    }

    /// <summary>
    /// The entry for exactly <paramref name="target"/>, which has already been through
    /// <see cref="LongPath.Configured(string?)"/>.
    ///
    /// <para>Asked in order of how specific the claim is: an address on this machine, then a
    /// position at the top of any volume, then a name found anywhere, then a type of file. The one
    /// written about this exact place is the one that was written about this thing, and the
    /// extension is the weakest claim of the four.</para>
    /// </summary>
    private KnownItem? Lookup(string target)
    {
        if (_byPath.TryGetValue(target, out var anchored))
        {
            return anchored;
        }

        if (AtTheTopOfAVolume(target) is { } reserved)
        {
            return reserved;
        }

        if (Path.GetFileName(target) is { Length: > 0 } name && _byName.TryGetValue(name, out var named))
        {
            return named;
        }

        return Path.GetExtension(target) is { Length: > 0 } extension
            ? _byExtension.GetValueOrDefault(extension)
            : null;
    }

    /// <summary>
    /// The <see cref="KnownPlace.VolumeRoot"/> entry for <paramref name="target"/>, or null where it
    /// is not one of them.
    ///
    /// <para>The path's trailing segments pick the one entry it could be, and only then is the
    /// machine asked whether those segments really start at the top of the path's volume
    /// (<see cref="_belowVolumeRootDepths"/> says why in that order). A path whose text is deeper
    /// than its volume root, which is every path on a volume mounted at a folder, is therefore
    /// answered by where the volume is mounted rather than by its drive letter.</para>
    /// </summary>
    private KnownItem? AtTheTopOfAVolume(string target)
    {
        foreach (var depth in _belowVolumeRootDepths)
        {
            if (Trailing(target, depth) is { } candidate
                && _belowVolumeRoot.TryGetValue(candidate, out var reserved)
                && candidate.Equals(VolumeRoot.Below(_volumes, target), StringComparison.OrdinalIgnoreCase))
            {
                return reserved;
            }
        }

        return null;
    }

    /// <summary>
    /// The last <paramref name="depth"/> segments of <paramref name="path"/>, or null where it has
    /// no more than that many — a path with nothing above those segments has no volume root above
    /// them either.
    /// </summary>
    private static string? Trailing(string path, int depth)
    {
        var start = path.Length;

        for (var remaining = depth; remaining > 0; remaining--)
        {
            start = path.LastIndexOf(Path.DirectorySeparatorChar, start - 1);

            if (start <= 0)
            {
                return null;
            }
        }

        return path[(start + 1)..];
    }

    private static int Depth(string relativePath) =>
        relativePath.Count(c => c == Path.DirectorySeparatorChar) + 1;

    /// <summary>
    /// Where <paramref name="entry"/> sits on this machine, or null where its place names no
    /// directory.
    ///
    /// <para>Put through <see cref="LongPath.Configured(string?)"/> rather than combined and
    /// trusted, for the reason that method exists: the anchors come from the environment, and a
    /// trailing separator or a <c>..</c> in one would make the key compare equal to nothing at
    /// all — an entry silently absent from the catalogue rather than a visible failure.</para>
    ///
    /// <para>It is also what makes an anchor naming no directory answer null here, with no separate
    /// check: <c>%ProgramFiles(x86)%</c> is empty on a 32-bit Windows, and combining an empty anchor
    /// with a relative path gives a path that is not fully qualified, which
    /// <see cref="LongPath.Configured(string?)"/> refuses.</para>
    /// </summary>
    private static string? Resolve(
        IReadOnlyDictionary<KnownPlace, string> anchors, KnownItem entry) =>
        anchors.TryGetValue(entry.Place, out var anchor)
            ? LongPath.Configured(
                entry.RelativePath.Length == 0 ? anchor : Path.Combine(anchor, entry.RelativePath))
            : null;

    /// <summary>
    /// Every place, read through the two seams. <c>C:\Users</c> is derived from the profile rather
    /// than assumed, because that is the only route to it that stays right when a profile has been
    /// moved.
    /// </summary>
    private static Dictionary<KnownPlace, string> Anchors(
        ISystemDirectories system, IUserEnvironment environment)
    {
        var anchors = new Dictionary<KnownPlace, string>
        {
            [KnownPlace.WindowsDirectory] = system.WindowsDirectory,
            [KnownPlace.ProgramFiles] = system.ProgramFiles,
            [KnownPlace.ProgramFilesX86] = system.ProgramFilesX86,
            [KnownPlace.ProgramData] = system.ProgramData,
            [KnownPlace.UserProfile] = environment.UserProfile,
            [KnownPlace.LocalAppData] = environment.LocalAppData,
            [KnownPlace.RoamingAppData] = environment.RoamingAppData,
        };

        // Absent rather than assembled when the platform will not say where the tier is, which is
        // the one application-data tier that can happen for.
        if (environment.LocalLowAppData is { Length: > 0 } localLow)
        {
            anchors[KnownPlace.LocalLowAppData] = localLow;
        }

        if (Path.GetDirectoryName(environment.UserProfile) is { Length: > 0 } users)
        {
            anchors[KnownPlace.UserProfiles] = users;
        }

        return anchors;
    }
}
