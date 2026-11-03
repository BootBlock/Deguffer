using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// §5.2 as §7.1 needs it read from outside, for one Mozilla application: what Explore must refuse
/// in the application's folders and in every profile its register names. Separate from
/// <see cref="MozillaProfileCacheProvider"/> because it answers a different question: the provider
/// decides what a plan removes, and this decides what a user may not remove by hand.
/// </summary>
/// <param name="application">The application whose profiles are declared.</param>
/// <param name="discovery">The application's register, already read for this pass.</param>
internal sealed class MozillaToolRoots(MozillaApplication application, MozillaProfileDiscovery discovery)
{
    /// <summary>
    /// Nothing is disposable in the roaming half. It is a <see cref="ToolRoot"/> so that §7.1's
    /// second deletion route refuses it too: Explore draws every directory on the drive and lets the
    /// user act on one, and "never the roaming profile at all" is not a rule that can hold on the
    /// Storage page alone.
    /// </summary>
    private static readonly DisposableChildSet NothingIsDisposable = new([]);

    private string App => application.Name;

    /// <summary>
    /// The application's own folder under each application-data root, then both halves of every
    /// profile — the local half declaring the caches, and everything else declaring nothing at all.
    ///
    /// <para>The roaming side is declared even though no plan is ever made against it, and that is
    /// the point. Explore's refusals come from these declarations, so without them a user could
    /// delete <c>logins.json</c> from the size picture while the Storage page was carefully leaving
    /// it alone.</para>
    ///
    /// <para><b>The folders above a profile are declared too, and refusing the profile alone is
    /// worth nothing without them.</b> <c>%APPDATA%\Mozilla\Firefox</c> and the <c>Profiles</c>
    /// directory under it hold every profile's password database between them, so a refusal that
    /// stopped at the profile would refuse a directory and permit its parent. They are declared
    /// unconditionally rather than from what discovery found, because a register that would not be
    /// read leaves no profiles and the files are on disk regardless — and a declaration naming a
    /// directory that is not there refuses nothing.</para>
    ///
    /// <para><b>A profile kept outside the application's folder is declared too</b>, whole, because
    /// nothing above it is the application's and so nothing else refuses it: <c>D:\Firefox\work</c>
    /// holds the same <c>logins.json</c> the declared profiles do. A profile at the top of a drive or
    /// share is <see cref="AtTheTopOfADrive"/>'s instead.</para>
    /// </summary>
    public IReadOnlyList<ToolRoot> Declared(
        IReadOnlyList<MozillaProfile> profiles,
        IEnumerable<(string Name, string Path)> elsewhere) =>
    [
        ToolRoot.Of(
            discovery.RoamingRoot,
            $"This is {App}'s own folder. Your profiles are inside it, with your "
            + $"{application.UserDataSummary}, and Deguffer removes nothing from any of them.",
            NothingIsDisposable),
        ToolRoot.Of(
            discovery.LocalRoot,
            $"This is where {App} keeps the caches for each of your profiles. Deguffer removes "
            + "them from the Storage page, where it knows which of them are caches.",
            NothingIsDisposable),
        .. from profile in profiles
           from root in new[]
           {
               ToolRoot.Of(
                   profile.LocalPath,
                   $"This is the cache folder for your '{profile.Name}' {App} profile. Deguffer "
                   + "removes the caches inside it from the Storage page, where it knows which of "
                   + "them are caches.",
                   application.Children),
               ToolRoot.Of(
                   profile.RoamingPath,
                   $"This is your '{profile.Name}' {App} profile — {application.UserDataSummary}. "
                   + "Nothing in here is a cache, and Deguffer removes nothing from it.",
                   NothingIsDisposable),
           }
           select root,
        .. from profile in elsewhere
           where Path.GetDirectoryName(profile.Path) is not null
           select ToolRoot.Of(
               profile.Path,
               $"This is your '{profile.Name}' {App} profile, which {App} keeps outside its own "
               + $"folder. Your {application.UserDataSummary} are in it, and Deguffer removes "
               + "nothing from it.",
               NothingIsDisposable),
    ];

    /// <summary>
    /// A profile at the top of a drive or share, which <see cref="Declared"/> leaves out: refusing
    /// every child of a drive would take the whole drive away from Explore, and a volume root is
    /// never itself removable there. The user data the plan protects by name is refused instead,
    /// and everything else on a local drive stays the user's (Explore refuses everything on a
    /// share). Declared through <see cref="ICleanupProvider.DiscoverToolRootsAsync"/>, because a
    /// declaration made there only ever adds a refusal. See <see cref="ToolRoot.Sparing"/>.
    /// </summary>
    public IReadOnlyList<ToolRoot> AtTheTopOfADrive(IEnumerable<(string Name, string Path)> elsewhere) =>
    [
        .. from profile in elsewhere
           where Path.GetDirectoryName(profile.Path) is null
           select ToolRoot.Sparing(
               profile.Path,
               $"Your '{profile.Name}' {App} profile is kept at the top of this drive, so its "
               + $"{application.UserDataSummary} are here. Deguffer removes none of them.",
               application.UserData.Select(data => data.Name)),
    ];
}
