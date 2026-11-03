using System.Text.RegularExpressions;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.ModManagers;

/// <summary>
/// Every Mod Organizer 2 instance this account's records lead to, and what stopped one being read.
/// </summary>
/// <param name="Instances">Each instance found, with the download folder its settings name.</param>
/// <param name="Problems">Why a record or an instance could not be read, one sentence each.</param>
internal sealed record ModOrganizerReading(IReadOnlyList<ModOrganizerInstance> Instances, IReadOnlyList<string> Problems)
{
    public static readonly ModOrganizerReading None = new([], []);
}

/// <summary>
/// Finds Mod Organizer 2's instances from where Mod Organizer 2 itself keeps them, and reads where each
/// keeps its downloads.
///
/// <para><b>Two kinds of instance.</b> A global instance is a folder in <c>%LOCALAPPDATA%\ModOrganizer</c>
/// holding <c>ModOrganizer.ini</c>, which is the test Mod Organizer 2's instance manager makes
/// (<c>instancemanager.cpp</c>, <c>globalInstancePaths</c>). A portable instance is a
/// <c>ModOrganizer.ini</c> beside <c>ModOrganizer.exe</c>, wherever that was unpacked, and nothing
/// records it but the download handler: each installation that handles Nexus Mods links writes the path
/// of its executable into <c>downloadhandler.ini</c> in the same folder
/// (<c>modorganizer-nxmhandler</c>, <c>HandlerStorage::saveStore</c>). A portable installation that
/// never registered as the handler is not found, and the report says so.</para>
///
/// <para><b>Where the downloads are.</b> <c>[Settings] download_directory</c>, which defaults to
/// <c>%BASE_DIR%/downloads</c>, and <c>%BASE_DIR%</c> is <c>base_directory</c>, which defaults to the
/// instance's own folder (<c>settings.cpp</c>). Mod Organizer 2 removes a key whose value is its default,
/// so both are usually missing. The folder a setting names is vetted by <see cref="ConfiguredFolder"/>,
/// because it may be a folder the user keeps other things in, such as their own Downloads.</para>
/// </summary>
internal static partial class ModOrganizerInstances
{
    /// <summary>The folder in <c>%LOCALAPPDATA%</c> Mod Organizer 2 keeps its global instances in.</summary>
    public const string FolderName = "ModOrganizer";

    /// <summary>The file that marks a folder as an instance.</summary>
    public const string SettingsName = "ModOrganizer.ini";

    /// <summary>The download handler's record of every installation registered to handle Nexus Mods links.</summary>
    public const string HandlerRecordName = "downloadhandler.ini";

    public const string SettingsSection = "Settings";

    public const string BaseKey = "base_directory";

    public const string DownloadsKey = "download_directory";

    public const string BaseVariable = "%BASE_DIR%";

    /// <summary>What <see cref="DownloadsKey"/> is where Mod Organizer 2 left it at its default.</summary>
    public const string DefaultDownloads = BaseVariable + "/downloads";

    /// <summary>
    /// Far past any settings file Mod Organizer 2 writes, which holds its tools, plugin settings and window
    /// layout beside the paths.
    /// </summary>
    private const int MaximumSettingsBytes = 4 * 1024 * 1024;

    private const int MaximumHandlerRecordBytes = 256 * 1024;

    public static string Root(IUserEnvironment environment) => Path.Combine(environment.LocalAppData, FolderName);

    public static ModOrganizerReading Read(IUserEnvironment environment, ISystemDirectories system, IVolumeInventory volumes)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(volumes);

        var root = Root(environment);

        switch (LongPath.ProbeDirectory(root))
        {
            case PathPresence.Absent:
                return ModOrganizerReading.None;
            case PathPresence.Refused:
                return new ModOrganizerReading([], [UnreadableRoot.WhyItCouldNotBeReached(root)]);
        }

        var instances = new List<ModOrganizerInstance>();
        var problems = new List<string>();
        var temporary = TempRoots.Resolve(environment, system, volumes).AccountFolders;

        var children = ChildDirectories.Under(root);

        if (children.Unreadable)
        {
            problems.Add(UnreadableRoot.Note(root).Message);
        }

        foreach (var child in children.Directories)
        {
            var folder = LongPath.Display(child.FullName);

            if (Instance(child.Name, folder, portable: false) is { } instance)
            {
                instances.Add(instance);
            }
        }

        foreach (var link in children.Links)
        {
            problems.Add(
                $"'{LongPath.Display(link.FullName)}' is a link, which Deguffer does not follow, so whether it holds a "
                + "Mod Organizer 2 instance was not looked at.");
        }

        foreach (var folder in PortableFolders(root, problems))
        {
            if (!instances.Any(known => known.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))
                && Instance(Path.GetFileName(folder), folder, portable: true) is { } instance)
            {
                instances.Add(instance);
            }
        }

        return new ModOrganizerReading(instances, problems);

        ModOrganizerInstance? Instance(string name, string folder, bool portable)
        {
            var settings = Path.Combine(folder, SettingsName);

            switch (LongPath.ProbeFile(settings))
            {
                case PathPresence.Absent:
                    return null;
                case PathPresence.Refused:
                    // A refusal is not evidence that this is an instance, and not evidence that it is not.
                    problems.Add(
                        $"{new UnreadFile(settings, Unreached: true).Opening("a Mod Organizer 2 instance's settings")}, "
                        + "so any downloads it keeps were not looked for.");
                    return null;
            }

            if (QtIniFile.Read(settings, MaximumSettingsBytes) is not { } ini)
            {
                problems.Add(
                    $"{new UnreadFile(settings, Unreached: false).Opening("a Mod Organizer 2 instance's settings")}, "
                    + "so where it keeps its downloads is not known.");
                return null;
            }

            return Located(name, folder, portable, ini);
        }

        ModOrganizerInstance Located(string name, string folder, bool portable, QtIniFile ini)
        {
            var baseValue = ini.Value(SettingsSection, BaseKey) is { Length: > 0 } setBase ? setBase : folder;
            var value = ini.Value(SettingsSection, DownloadsKey) is { Length: > 0 } set ? set : DefaultDownloads;

            if (LongPath.Configured(value.Replace(BaseVariable, baseValue, StringComparison.Ordinal)) is not { } named)
            {
                return new ModOrganizerInstance(name, folder, portable, null, null, ConfiguredFolder.NotAFullPath);
            }

            named = LongPath.Display(named);

            return ConfiguredFolder.WhyNotOwned(named, environment, system, volumes, temporary) is { } why
                ? new ModOrganizerInstance(name, folder, portable, named, null, why)
                : new ModOrganizerInstance(name, folder, portable, named, named, null);
        }
    }

    /// <summary>
    /// The folder of each <c>ModOrganizer.exe</c> the download handler's record names. Each one holding
    /// <see cref="SettingsName"/> is a portable instance; the rest are installations whose instances are
    /// global, and are found there.
    /// </summary>
    private static IEnumerable<string> PortableFolders(string root, List<string> problems)
    {
        var record = Path.Combine(root, HandlerRecordName);

        if (LongPath.ProbeFile(record) is var presence and not PathPresence.Present)
        {
            if (presence == PathPresence.Refused)
            {
                problems.Add(
                    $"{new UnreadFile(record, Unreached: true).Opening("Mod Organizer 2's download handler record")}, "
                    + "so a portable Mod Organizer 2 it names was not looked for.");
            }

            return [];
        }

        if (QtIniFile.Read(record, MaximumHandlerRecordBytes) is not { } ini)
        {
            problems.Add(
                $"{new UnreadFile(record, Unreached: false).Opening("Mod Organizer 2's download handler record")}, "
                + "so a portable Mod Organizer 2 it names was not looked for.");
            return [];
        }

        return ini.Section("handlers")
            .Where(entry => HandlerExecutable().IsMatch(entry.Key))
            .Select(entry => LongPath.Configured(entry.Value) is { } executable && Path.GetDirectoryName(executable) is { } folder
                ? LongPath.Display(folder)
                : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>An installation's executable in the handler record's array, as in <c>1\executable</c>.</summary>
    [GeneratedRegex(@"^\d+\\executable$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HandlerExecutable();
}
