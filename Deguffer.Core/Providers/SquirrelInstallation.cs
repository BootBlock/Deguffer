using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>One installed version of a Squirrel application, as it appears on disk.</summary>
/// <param name="Path">The directory, in display form.</param>
/// <param name="Name">Its own name, <c>app-3.6.4</c>, which is what the user sees in the folder.</param>
/// <param name="Number">
/// The version parsed out of that name. Both of Squirrel's launch paths order builds by these same
/// numbers, so ordering them is reading the application's own rule rather than inventing one.
/// </param>
/// <param name="IsLink">
/// Whether this is a junction or a symbolic link rather than a directory.
///
/// <para>Such a build is counted when the versions are ordered and never removed, and it needs both
/// halves. Dropping it from the ordering is how the newest build gets named superseded. Removing it
/// takes a link whose far side nobody classified — and the figure beside it was measured
/// <em>through</em> the link, so the row would promise the far side's size and reclaim none of
/// it.</para>
/// </param>
/// <param name="UnfinishedMarker">
/// Whether anything named <see cref="SquirrelDiscovery.UnfinishedMarkerName"/> is inside it, of
/// either kind, because the stub skips a build on any entry by that name. Probed through a link as
/// through a directory, because the stub follows the link to look for it too.
/// </param>
public sealed record SquirrelVersionDirectory(
    string Path,
    string Name,
    Version Number,
    bool IsLink,
    PathPresence UnfinishedMarker);

/// <summary>
/// Why an installation's builds have no order Deguffer can act on, which is what makes
/// <see cref="SquirrelInstallation.Current"/> null. Where several apply, the first in this order is
/// the one reported.
/// </summary>
public enum SquirrelOrderDoubt
{
    /// <summary>The newest build is the one both launch paths start.</summary>
    None,

    /// <summary>A build's version could not be read, so the builds cannot be ordered at all.</summary>
    UnreadableVersion,

    /// <summary>
    /// Two builds are named alike but for case, which a case-sensitive folder can hold. Both launch
    /// paths find a build by name, and which of the two they reach is not something Deguffer can say.
    /// </summary>
    AmbiguousBuilds,

    /// <summary>A build holds the marker the updater removes once it has finished unpacking it.</summary>
    UnfinishedUpdate,

    /// <summary>Windows would not say whether a build holds that marker.</summary>
    UnfinishedUnknown,

    /// <summary>Windows would not say whether the index is there.</summary>
    IndexUnreached,

    /// <summary>The index is there and could not be read.</summary>
    IndexUnreadable,

    /// <summary>
    /// The index leads <c>Update.exe --processStart</c> to an older build or to none, which is what an
    /// update that stopped after it unpacked a build and before it recorded it leaves behind.
    /// </summary>
    IndexBehind,

    /// <summary>
    /// The index names a version Deguffer cannot order against the others, so which build it leads
    /// to has no answer.
    /// </summary>
    IndexUnorderable,
}

/// <summary>
/// One Squirrel-installed application under <c>%LOCALAPPDATA%</c>, and the version directories in
/// it.
/// </summary>
/// <param name="Name">
/// The folder's own name, which is the application's: Squirrel installs into a directory named for
/// the package, so this is the only label available and the one the user will recognise.
/// </param>
/// <param name="Root">The application folder itself, in display form. Never a target.</param>
/// <param name="Versions">
/// The version directories whose version could be read, oldest first.
/// </param>
/// <param name="UnreadableVersionNames">
/// Children named like a version directory whose version could not be read — a pre-release build
/// such as <c>app-2.0.0-beta1</c>. Kept rather than dropped because their presence is what makes
/// <see cref="Superseded"/> empty: see the property for why that has to fail closed.
/// </param>
/// <param name="Index">The application's <c>packages\RELEASES</c>, as one read found it.</param>
public sealed record SquirrelInstallation(
    string Name,
    string Root,
    IReadOnlyList<SquirrelVersionDirectory> Versions,
    IReadOnlyList<string> UnreadableVersionNames,
    SquirrelReleaseIndex Index)
{
    /// <summary>
    /// Whether the newest build is the one the application starts, and if not, why not.
    ///
    /// <para><b>An application starts one of two ways, and they agree only once an update has
    /// finished.</b> The stub in the application's own folder starts the newest build that does not
    /// hold <see cref="SquirrelDiscovery.UnfinishedMarkerName"/>, which the updater writes into a
    /// build before it unpacks it and removes after. <c>Update.exe --processStart</c>, which many
    /// shortcuts use, starts the newest build the index names, and the updater rewrites the index
    /// only after that. An update that stopped part-way — a crash, a power cut, a full disk — leaves
    /// a newest build neither path starts, and the one they do start is the one an ordering by
    /// version alone would offer as superseded.</para>
    ///
    /// <para>An index that is absent is not a doubt. <c>--processStart</c> then starts nothing
    /// whichever builds are on disk, so removing one changes nothing for it, and the stub's rule
    /// decides alone.</para>
    /// </summary>
    public SquirrelOrderDoubt Doubt { get; } = Judge(Versions, UnreadableVersionNames, Index);

    /// <summary>
    /// The newest installed version, which is the one the application launches, or null where
    /// <see cref="Doubt"/> says the set has no order to act on.
    /// </summary>
    public SquirrelVersionDirectory? Current =>
        Doubt is SquirrelOrderDoubt.None ? Versions.LastOrDefault() : null;

    /// <summary>
    /// The versions an update left behind, which is every one that is not <see cref="Current"/>.
    ///
    /// <para><b>Empty whenever <see cref="Doubt"/> is anything but none, and that is the whole
    /// safety property here.</b> A pre-release version orders below its own release under one
    /// reading and above it under another, so an installation holding <c>app-1.2.3</c> beside
    /// <c>app-1.3.0-beta1</c> has no answer this can give; an unfinished update leaves the newest
    /// build one neither launch path starts. Calling the highest build current in either case
    /// would name the <em>running</em> build superseded, and removing it leaves the user without
    /// the application.</para>
    /// </summary>
    public IReadOnlyList<SquirrelVersionDirectory> Superseded =>
        Current is { } current ? [.. Versions.Where(v => v != current)] : [];

    private static SquirrelOrderDoubt Judge(
        IReadOnlyList<SquirrelVersionDirectory> versions,
        IReadOnlyList<string> unreadableVersionNames,
        SquirrelReleaseIndex index)
    {
        if (unreadableVersionNames.Count > 0)
        {
            return SquirrelOrderDoubt.UnreadableVersion;
        }

        if (versions.DistinctBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Count() < versions.Count)
        {
            return SquirrelOrderDoubt.AmbiguousBuilds;
        }

        // Any build rather than only the newest. An update that stopped part-way and was followed by
        // a newer one leaves the marker in an older build until a clean-up whose failures the
        // updater ignores, and a build the stub will not start is not one to reason past.
        if (versions.Any(v => v.UnfinishedMarker is PathPresence.Present))
        {
            return SquirrelOrderDoubt.UnfinishedUpdate;
        }

        if (versions.Any(v => v.UnfinishedMarker is PathPresence.Refused))
        {
            return SquirrelOrderDoubt.UnfinishedUnknown;
        }

        return index.State switch
        {
            SquirrelIndexState.Absent => SquirrelOrderDoubt.None,
            SquirrelIndexState.Unreached => SquirrelOrderDoubt.IndexUnreached,
            SquirrelIndexState.Read => index.DoubtAbout(versions),
            _ => SquirrelOrderDoubt.IndexUnreadable,
        };
    }
}
