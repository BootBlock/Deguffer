using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The <c>profiles.ini</c> of every installation of one Mozilla application, read together: which
/// profiles they name, which they keep elsewhere, which registers would not be read, and the
/// sentences and declarations each installation owes about its own folders.
///
/// <para>Separate from <see cref="MozillaProfileCacheProvider"/> because it answers a different
/// question: this says what the registers established, and the provider decides what may go inside a
/// profile they named. Each installation is read through its own register and its own two roots, so
/// nothing one of them finds is resolved under another's.</para>
/// </summary>
internal sealed class MozillaRegisters
{
    private readonly MozillaApplication _application;
    private readonly Installed[] _installed;

    public MozillaRegisters(MozillaApplication application, IUserEnvironment environment)
    {
        _application = application;
        _installed =
        [
            .. from installation in application.Installations
               let discovery = new MozillaProfileDiscovery(environment, installation)
               select new Installed(discovery, new MozillaToolRoots(application, discovery)),
        ];
        ProcessNames = [.. application.Installations.Select(installation => installation.ProcessName)];
    }

    /// <summary>Every installation's process, for §5.3.</summary>
    public IReadOnlyList<string> ProcessNames { get; }

    /// <summary>
    /// Every profile every register names, with both halves resolved. Reading is what fills
    /// <see cref="ProfilesElsewhere"/> and <see cref="Unread"/>, so those are asked after it.
    /// </summary>
    public IReadOnlyList<MozillaProfile> Discover(CancellationToken ct) =>
        [.. _installed.SelectMany(installed => installed.Discovery.Discover(ct))];

    /// <summary>Every profile any installation keeps outside its own folder.</summary>
    public IReadOnlyList<MozillaProfileElsewhere> ProfilesElsewhere() =>
        [.. _installed.SelectMany(installed => installed.Discovery.ProfilesElsewhere)];

    /// <summary>
    /// The registers Windows would not describe or would not let be read, which leave their
    /// installation's profiles unknown rather than absent.
    /// </summary>
    public IReadOnlyList<MozillaProfileDiscovery> Unread() =>
    [
        .. from installed in _installed
           where installed.Discovery.Register is MozillaRegisterReading.Unreached or MozillaRegisterReading.Unreadable
           select installed.Discovery,
    ];

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: each installation's folders, and both halves of each
    /// profile under the installation whose register named it. See <see cref="MozillaToolRoots.Declared"/>.
    /// </summary>
    public IReadOnlyList<ToolRoot> Declared(IReadOnlyList<MozillaProfile> profiles) =>
    [
        .. _installed.SelectMany(installed => installed.Roots.Declared(
            [.. profiles.Where(profile => profile.Installation == installed.Discovery.Installation)],
            LocatedElsewhere(installed.Discovery))),
    ];

    /// <summary>Each profile kept at the top of a drive or share. See <see cref="MozillaToolRoots.AtTheTopOfADrive"/>.</summary>
    public IReadOnlyList<ToolRoot> AtTheTopOfADrive() =>
    [
        .. _installed.SelectMany(installed =>
            installed.Roots.AtTheTopOfADrive(LocatedElsewhere(installed.Discovery))),
    ];

    /// <summary>
    /// §5.6 for the folders of every installation whose register is on disk, read or not. One whose
    /// register is absent has no profile for a plan to protect, though <see cref="Declared"/> still
    /// declares its folders to Explore.
    /// </summary>
    public IEnumerable<(string Path, string Reason)> FolderSurvivors()
    {
        foreach (var installed in _installed.Where(i => i.Discovery.Register is not MozillaRegisterReading.Absent))
        {
            var discovery = installed.Discovery;
            var app = discovery.Installation.Name;

            yield return (
                discovery.RoamingRoot,
                $"{app}'s own folder, which holds the register of your profiles and the profiles "
                + "themselves. Nothing is ever removed from it.");
            yield return (
                discovery.ProfilesPath,
                $"{app}'s register of your profiles. Removing it would lose every profile.");
            yield return (
                discovery.LocalRoot,
                $"{app}'s cache folder itself must survive — only recognised caches inside a profile "
                + "under it are removed.");
        }
    }

    /// <summary>
    /// §5.6 for the profiles kept outside an installation's folder: each directory, and the user data
    /// inside it by name, for the reason <see cref="MozillaApplication.UserData"/> gives.
    /// </summary>
    public IEnumerable<(string Path, string Reason)> SurvivorsElsewhere() =>
        from installed in _installed
        let app = installed.Discovery.Installation.Name
        from profile in LocatedElsewhere(installed.Discovery)
        from survivor in _application.UserDataIn(profile.Path)
            .Prepend((
                profile.Path,
                $"Your '{profile.Name}' {app} profile, kept outside {app}'s own folder. Deguffer "
                + "removes nothing from it."))
        select survivor;

    /// <summary>
    /// The sentence for the profiles each installation's <c>profiles.ini</c> puts outside its own
    /// folder, one per installation. Said in two places, because such a profile can be one of several
    /// or the only one there is, and the second case returns before the plan is ever built.
    /// </summary>
    public IEnumerable<string> StoredElsewhere(IReadOnlyList<MozillaProfileElsewhere> profiles) =>
        from installation in profiles.GroupBy(profile => profile.Installation)
        let app = installation.Key.Name
        let count = installation.Count()
        select $"The {Listed.And([.. installation.Select(profile => $"'{profile.Name}'")])} {app} "
            + $"{Counted.Form(count, "profile is", "profiles are")} kept outside {app}'s own folder. "
            + $"Deguffer did not examine {Counted.Form(count, "it", "them")}: a profile stored that way "
            + $"keeps its cache among your {_application.UserDataSummary} rather than separately from them.";

    /// <summary>The warning for one installation's register that would not be read.</summary>
    public static PlanNote RegisterNote(MozillaProfileDiscovery discovery)
    {
        var app = discovery.Installation.Name;
        var register = new UnreadFile(
            discovery.ProfilesPath,
            Unreached: discovery.Register is MozillaRegisterReading.Unreached);

        return new PlanNote(
            PlanNoteSeverity.Warning,
            register.Opening($"{app}'s list of profiles")
            + $", so Deguffer could not work out which {app} profiles exist. Nothing was planned, "
            + "and nothing was ruled out either.");
    }

    /// <summary>
    /// What a profile's caches are listed under: the profile's own name, which is what the user sees
    /// in the application's profile manager, with the installation's name before it where the row
    /// has more than one, because a Zen profile and a Firefox profile can both be called
    /// <c>default-release</c>.
    /// </summary>
    public string Heading(MozillaProfile profile) =>
        _installed.Length > 1 ? $"{profile.Installation.Name} — {profile.Name}" : profile.Name;

    /// <summary>
    /// Each profile one installation keeps outside its own folder that names a directory, with that
    /// directory.
    /// </summary>
    private static IEnumerable<(string Name, string Path)> LocatedElsewhere(MozillaProfileDiscovery discovery)
    {
        foreach (var profile in discovery.ProfilesElsewhere)
        {
            if (profile.Path is { } path)
            {
                yield return (profile.Name, path);
            }
        }
    }

    /// <summary>One installation's register, and what Explore must refuse around it.</summary>
    private sealed record Installed(MozillaProfileDiscovery Discovery, MozillaToolRoots Roots);
}
