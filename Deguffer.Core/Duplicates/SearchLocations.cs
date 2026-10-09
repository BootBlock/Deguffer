using Deguffer.Core.Safety;

namespace Deguffer.Core.Duplicates;

/// <summary>A chosen location, resolved to where it really is.</summary>
/// <param name="Given">The location as the user chose it.</param>
/// <param name="Folder">
/// Where it really is, in display form, once every link and substituted drive on the way to it is
/// followed: the form every location of the search is compared in.
/// </param>
/// <param name="Reached">The folder at every path it is reachable at.</param>
/// <param name="Role">Its role, which is a reference where the folder was given in both roles.</param>
public sealed record ResolvedLocation(SearchLocation Given, string Folder, ReachedFolder Reached, LocationRole Role);

/// <summary>A chosen location the search will not enumerate, and why, in a sentence the page shows.</summary>
public sealed record UnsearchedLocation(SearchLocation Given, string Reason);

/// <summary>
/// The locations of a search, each resolved to where it is before anything is enumerated (§7.4).
///
/// <para><b>Resolved, because a file found twice is how a finder lists a file as its own
/// duplicate.</b> Two locations can reach one folder by two names: a junction or a symbolic link
/// anywhere in the path, a substituted drive, a volume mounted in a folder, or the same folder named
/// twice. Each location is followed to the final path of an opened handle, which is in the one form
/// every link resolves to, and a location that is another one, or lies inside another, is enumerated
/// as part of it rather than again.</para>
///
/// <para><b>Compared as Windows names the folders, case and all.</b> NTFS lets a folder be
/// case-sensitive, so <c>Photos</c> and <c>photos</c> can be two folders; each final path carries
/// the case the disk holds, so the comparison is ordinal. The places a folder is also reachable at,
/// through a volume mounted in more than one place, are asked of <see cref="ReachedFolder"/> as well,
/// for a folder whose final path Windows gives through a different mount.</para>
///
/// <para><b>Local disks only.</b> A share can be this machine's own disk under a second name that no
/// identity reveals, and a cloud client's drive downloads what it is asked to read, so a location on
/// either is refused with its reason, as Explore refuses to draw them.</para>
/// </summary>
public sealed class SearchLocations
{
    private SearchLocations(
        IReadOnlyList<ResolvedLocation> locations,
        IReadOnlyList<ResolvedLocation> roots,
        IReadOnlyList<UnsearchedLocation> unsearched)
    {
        Locations = locations;
        Roots = roots;
        Unsearched = unsearched;
    }

    /// <summary>Every location the search covers, each folder once.</summary>
    public IReadOnlyList<ResolvedLocation> Locations { get; }

    /// <summary>The locations no other one holds: what is enumerated, each once.</summary>
    public IReadOnlyList<ResolvedLocation> Roots { get; }

    /// <summary>The locations that are not searched, each with its reason.</summary>
    public IReadOnlyList<UnsearchedLocation> Unsearched { get; }

    /// <summary>The locations strictly inside <paramref name="root"/>, which its enumeration reaches.</summary>
    public IReadOnlyList<ResolvedLocation> Within(ResolvedLocation root) =>
        [.. Locations.Where(inner => !ReferenceEquals(inner, root) && Holds(root.Folder, inner.Folder))];

    /// <summary>Resolve each of <paramref name="given"/>, in order.</summary>
    public static SearchLocations Resolve(IReadOnlyList<SearchLocation> given, IVolumeInventory volumes)
    {
        ArgumentNullException.ThrowIfNull(given);
        ArgumentNullException.ThrowIfNull(volumes);

        List<ResolvedLocation> resolved = [];
        List<UnsearchedLocation> unsearched = [];

        foreach (var location in given)
        {
            if (Resolve(location, volumes) is { } why)
            {
                if (why.Location is { } found)
                {
                    Merge(resolved, found);
                }
                else
                {
                    unsearched.Add(new UnsearchedLocation(location, why.Reason!));
                }
            }
        }

        return new SearchLocations(
            resolved,
            [.. resolved.Where(location => !resolved.Exists(outer => !ReferenceEquals(outer, location) && Holds(outer.Folder, location.Folder)))],
            unsearched);
    }

    /// <summary>
    /// Whether <paramref name="outer"/> is <paramref name="inner"/> or holds it, as their final paths
    /// say: ordinally, because a folder may be case-sensitive.
    /// </summary>
    internal static bool Holds(string outer, string inner)
    {
        if (inner.Equals(outer, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(outer) ? outer : outer + Path.DirectorySeparatorChar;

        return inner.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static (ResolvedLocation? Location, string? Reason)? Resolve(SearchLocation location, IVolumeInventory volumes)
    {
        if (LongPath.Configured(location.Path) is not { } configured)
        {
            return (null, "This is not a full path to a drive or a folder.");
        }

        var followed = VolumeRoot.Followed(volumes, configured);

        // Before the handle is opened, because opening a file on a share is itself a conversation with
        // another machine, and the share is refused whatever it would say.
        if (LongPath.IsShare(followed))
        {
            return (null, NotLocal);
        }

        if (FileInformation.FinalPath(followed) is not { } final)
        {
            // Not "it is not there": a refusal reads the same, and the folder may well be there.
            return (null, "Windows would not open this folder, so Deguffer cannot tell where it is or what it holds.");
        }

        if (Displayed(final, volumes) is not { } folder)
        {
            return (null, "Windows names no drive or folder this volume is mounted at, so Deguffer cannot search it.");
        }

        if (LongPath.IsShare(folder))
        {
            return (null, NotLocal);
        }

        switch (LongPath.ProbeDirectory(folder))
        {
            case PathPresence.Absent:
                return (null, "This is a file, not a drive or a folder.");
            case PathPresence.Refused:
                return (null, "Windows would not say whether this is a folder, so Deguffer will not search it.");
        }

        if (WhyNotLocal(HostVolume.For(volumes, folder)) is { } notLocal)
        {
            return (null, notLocal);
        }

        return (new ResolvedLocation(location, folder, ReachedFolder.At(folder, volumes), location.Role), null);
    }

    private const string NotLocal =
        "This is on a network share. Deguffer searches only the disks of this computer, because a "
        + "file on a share can be one of this computer's own files under a second name.";

    private static string? WhyNotLocal(LocalVolume? volume) => volume switch
    {
        null => "Deguffer cannot tell which drive this is on, so it cannot tell whether its files are on this computer.",
        { StoresContentRemotely: true } =>
            "This drive keeps its files in the cloud. Reading one would download it, and removing one "
            + "removes it from the cloud, so Deguffer does not search it.",
        { IsReady: false } => "This drive is not ready, so Deguffer cannot search it.",
        { IsLocalDisk: false } =>
            "This is not a disk in or attached to this computer, so Deguffer does not search it.",
        _ => null,
    };

    /// <summary>
    /// <paramref name="final"/> in display form, with a volume Windows names by its GUID named by
    /// where it is mounted instead, or null where nothing names it.
    /// </summary>
    private static string? Displayed(string final, IVolumeInventory volumes)
    {
        var display = LongPath.Display(final);

        // A drive keeps its separator, which is how C:\ names a drive rather than a path relative to one.
        if (Path.IsPathFullyQualified(display))
        {
            return Path.TrimEndingDirectorySeparator(display);
        }

        foreach (var volume in volumes.Volumes)
        {
            if (volume.VolumeName is { } name && final.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                return Path.TrimEndingDirectorySeparator(Path.Join(volume.RootPath, final[name.Length..]));
            }
        }

        return null;
    }

    /// <summary>
    /// Adds <paramref name="found"/> to <paramref name="resolved"/>, or folds it into the location that
    /// is already the same folder, which becomes a reference where either was one.
    /// </summary>
    private static void Merge(List<ResolvedLocation> resolved, ResolvedLocation found)
    {
        var index = resolved.FindIndex(known => IsSameFolder(known, found));

        if (index < 0)
        {
            resolved.Add(found);
        }
        else if (found.Role == LocationRole.Reference)
        {
            resolved[index] = resolved[index] with { Role = LocationRole.Reference };
        }
    }

    /// <summary>
    /// Whether two locations are one folder: the same final path, or, where the two paths differ by
    /// more than case, two of the places one folder is reachable at.
    /// </summary>
    private static bool IsSameFolder(ResolvedLocation known, ResolvedLocation found) =>
        known.Folder.Equals(found.Folder, StringComparison.Ordinal)
        || (!known.Folder.Equals(found.Folder, StringComparison.OrdinalIgnoreCase) && known.Reached.IsSameAs(found.Reached));
}
