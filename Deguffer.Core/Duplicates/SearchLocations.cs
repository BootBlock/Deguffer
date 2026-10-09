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
/// <param name="Volume">
/// The volume <paramref name="Folder"/> is stored on, as <see cref="HostVolume.For"/> answers it. A
/// location on one volume never holds a location on another, whatever their paths say.
/// </param>
public sealed record ResolvedLocation(
    SearchLocation Given, string Folder, ReachedFolder Reached, LocationRole Role, LocalVolume Volume);

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
/// <para><b>Held only on one volume.</b> A volume mounted in a folder is named by that folder where
/// it has no drive letter, so <c>C:\mnt\v\Photos</c> reads as inside <c>C:\</c> while its files are
/// on another volume. The enumeration of <c>C:\</c> never crosses the mount point, which is a link in
/// its tree, so a location held by text alone would be enumerated by nothing, and the search would
/// read as one that found nothing there. A location holds another only where both are on one
/// volume, and one on another volume is enumerated in its own right.</para>
///
/// <para><b>Local disks only.</b> A share can be this machine's own disk under a second name that no
/// identity reveals, and a cloud client's drive downloads what it is asked to read, so a location on
/// either is refused with its reason, as Explore refuses to draw them.</para>
///
/// <para><b>A reference it could not resolve still names a place.</b> That place is kept
/// (<see cref="UnresolvedReferences"/>), so a location holding it passes over it rather than give
/// its files the holding location's role.</para>
/// </summary>
public sealed class SearchLocations
{
    private SearchLocations(
        IReadOnlyList<ResolvedLocation> locations,
        IReadOnlyList<ResolvedLocation> roots,
        IReadOnlyList<UnsearchedLocation> unsearched,
        UnresolvedReferences unresolvedReferences)
    {
        Locations = locations;
        Roots = roots;
        Unsearched = unsearched;
        UnresolvedReferences = unresolvedReferences;
    }

    /// <summary>Every location the search covers, each folder once.</summary>
    public IReadOnlyList<ResolvedLocation> Locations { get; }

    /// <summary>The locations no other one holds: what is enumerated, each once.</summary>
    public IReadOnlyList<ResolvedLocation> Roots { get; }

    /// <summary>The locations that are not searched, each with its reason.</summary>
    public IReadOnlyList<UnsearchedLocation> Unsearched { get; }

    /// <summary>The places of the reference locations among <see cref="Unsearched"/>, which a location holding one passes over.</summary>
    internal UnresolvedReferences UnresolvedReferences { get; }

    /// <summary>The locations strictly inside <paramref name="root"/>, which its enumeration reaches.</summary>
    public IReadOnlyList<ResolvedLocation> Within(ResolvedLocation root) =>
        [.. Locations.Where(inner => !ReferenceEquals(inner, root) && Holds(root, inner))];

    /// <summary>Resolve each of <paramref name="given"/>, in order.</summary>
    public static SearchLocations Resolve(IReadOnlyList<SearchLocation> given, IVolumeInventory volumes) =>
        Resolve(given, volumes, FileInformation.Default);

    /// <param name="files">
    /// Where each location is opened to learn its final path, so a test can see what is opened, and
    /// that a location refused before opening is not.
    /// </param>
    internal static SearchLocations Resolve(IReadOnlyList<SearchLocation> given, IVolumeInventory volumes, FileInformation files)
    {
        ArgumentNullException.ThrowIfNull(given);
        ArgumentNullException.ThrowIfNull(volumes);

        List<ResolvedLocation> resolved = [];
        List<UnsearchedLocation> unsearched = [];
        List<string> unresolvedReferences = [];

        foreach (var location in given)
        {
            var (found, reason, place) = Resolve(location, volumes, files);

            if (found is not null)
            {
                Merge(resolved, found);
                continue;
            }

            unsearched.Add(new UnsearchedLocation(location, reason!));

            if (location.Role == LocationRole.Reference && place is not null)
            {
                unresolvedReferences.Add(place);
            }
        }

        return new SearchLocations(
            resolved,
            [.. resolved.Where(location => !resolved.Exists(outer => !ReferenceEquals(outer, location) && Holds(outer, location)))],
            unsearched,
            new UnresolvedReferences(unresolvedReferences));
    }

    /// <summary>
    /// Whether <paramref name="outer"/> is <paramref name="inner"/> or holds it: both on one volume,
    /// and the final paths saying so ordinally, because a folder may be case-sensitive. See the class
    /// comment for why the volume is asked as well as the text.
    /// </summary>
    internal static bool Holds(ResolvedLocation outer, ResolvedLocation inner) =>
        outer.Volume.RootPath.Equals(inner.Volume.RootPath, StringComparison.OrdinalIgnoreCase)
        && LongPath.Contains(outer.Folder, inner.Folder, StringComparison.Ordinal);

    /// <summary>
    /// <paramref name="location"/> resolved, or why it is not, and the place it names: its path once a
    /// substituted drive is followed, in display form, or null where it is no full path. The place is
    /// what <see cref="UnresolvedReferences"/> passes over where a reference is not resolved.
    /// </summary>
    private static (ResolvedLocation? Location, string? Reason, string? Place) Resolve(
        SearchLocation location, IVolumeInventory volumes, FileInformation files)
    {
        if (LongPath.Configured(location.Path) is not { } configured)
        {
            return (null, "This is not a full path to a drive or a folder.", null);
        }

        var followed = VolumeRoot.Followed(volumes, configured);
        var (found, reason) = Resolve(location, followed, volumes, files);

        return (found, reason, Path.TrimEndingDirectorySeparator(LongPath.Display(followed)));
    }

    /// <param name="followed">The location's path, with a substituted drive followed.</param>
    private static (ResolvedLocation? Location, string? Reason) Resolve(
        SearchLocation location, string followed, IVolumeInventory volumes, FileInformation files)
    {
        if (WhyNotOpened(followed, volumes) is { } unopened)
        {
            return (null, unopened);
        }

        if (files.FinalPath(followed) is not { } final)
        {
            // Not "it is not there": a refusal reads the same, and the folder may well be there.
            return (null, "Windows would not open this folder, so Deguffer cannot tell where it is or what it holds.");
        }

        if (Displayed(final) is not { } folder)
        {
            return (null, "Windows names no drive or folder this volume is mounted at, so Deguffer cannot search it.");
        }

        // Again, because a link on a local disk can lead to a share, and the path as named did not say so.
        if (LongPath.IsShare(folder))
        {
            return (null, NotLocal);
        }

        switch (LongPath.ProbeDirectory(folder))
        {
            case PathPresence.Absent:
                return (null, "This is not a folder: it is a file, or it is no longer there.");
            case PathPresence.Refused:
                return (null, "Windows would not say whether this is a folder, so Deguffer will not search it.");
        }

        if (HostVolume.For(volumes, folder) is not { } volume)
        {
            return (null, "Deguffer cannot tell which drive this is on, so it cannot tell whether its files are on this computer.");
        }

        if (WhyNotLocal(volume) is { } notLocal)
        {
            return (null, notLocal);
        }

        return (new ResolvedLocation(location, folder, ReachedFolder.At(folder, volumes), location.Role, volume), null);
    }

    private const string NotLocal =
        "This is on a network share. Deguffer searches only the disks of this computer, because a "
        + "file on a share can be one of this computer's own files under a second name.";

    private const string InTheCloud =
        "This drive keeps its files in the cloud. Reading one would download it, and removing one "
        + "removes it from the cloud, so Deguffer does not search it.";

    /// <summary>
    /// Why <paramref name="followed"/> is refused before anything is opened on it, or null where only
    /// its final path can say.
    ///
    /// <para>A share, and a drive letter Windows maps to one, because opening anything there is
    /// itself a conversation with another machine, and the location is refused whatever it would say.
    /// A drive that keeps its files in the cloud, because its driver answers an open, and such a drive
    /// holds no link that could lead back to a local disk. Every other drive waits for the final path:
    /// a link on it can lead to a local disk, where the location is searched.</para>
    /// </summary>
    internal static string? WhyNotOpened(string followed, IVolumeInventory volumes) =>
        LongPath.IsShare(followed)
            ? NotLocal
            : HostVolume.For(volumes, followed) switch
            {
                { Kind: DriveType.Network } => NotLocal,
                { StoresContentRemotely: true } => InTheCloud,
                _ => null,
            };

    private static string? WhyNotLocal(LocalVolume volume) => volume switch
    {
        { StoresContentRemotely: true } => InTheCloud,
        { Kind: DriveType.Network } => NotLocal,
        { IsReady: false } => "This drive is not ready, so Deguffer cannot search it.",
        { IsLocalDisk: false } =>
            "This is not a disk in or attached to this computer, so Deguffer does not search it.",
        _ => null,
    };

    /// <summary>
    /// <paramref name="final"/> in display form, or null where Windows gave it only as a device path,
    /// such as a volume's <c>\\?\Volume{GUID}\</c> name, which names no drive or folder.
    ///
    /// <para><b>Measured on 2026-10-09</b> on an NTFS volume mounted in a folder with no drive
    /// letter: Windows gives the final path of a folder on it, and of a junction leading into it,
    /// through the folder the volume is mounted at, so a volume Windows names only by its GUID is
    /// one mounted nowhere a user can name.</para>
    /// </summary>
    internal static string? Displayed(string final)
    {
        var display = LongPath.Display(final);

        // A drive keeps its separator, which is how C:\ names a drive rather than a path relative to one.
        return Path.IsPathFullyQualified(display) && !LongPath.IsDeviceSpelling(display)
            ? Path.TrimEndingDirectorySeparator(display)
            : null;
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
