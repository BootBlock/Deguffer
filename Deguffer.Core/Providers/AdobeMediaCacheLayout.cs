using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where Adobe's video and audio applications keep the media cache they share, on this machine.
///
/// <para><b>The location is a setting, and Adobe keeps it in the registry.</b> Each release files it
/// under <c>HKCU\Software\Adobe\Common &lt;version&gt;\Media Cache</c>, as <c>FolderPath</c> for the
/// cache files and <c>DatabasePath</c> for the database. The version moves between releases, so every
/// <c>Common</c> key is read. Both values name the folder Adobe writes <em>into</em>: an exported
/// default reads <c>...\AppData\Roaming\Adobe\Common\</c> for both, while the files are in
/// <c>Media Cache Files</c> and the database in <c>Media Cache</c> inside it. So the folder a value
/// names is the user's own, and only Adobe's folder inside it is recognised.</para>
///
/// <para><b>The default folder is always examined, whatever the settings say.</b> Adobe keeps each
/// file's importer state (<c>.ims</c>) on the system drive when the cache is moved, and only the
/// converted audio and the waveforms follow the setting, so a moved cache leaves part of itself
/// behind.</para>
///
/// <para><b>§5.2.</b> <c>%APPDATA%\Adobe\Common</c> is never a target. Beside the cache it holds the
/// user's LUTs, installed motion graphics templates and Team Projects' auto-save history, and the whole
/// of what else Adobe keeps there is not established. Nothing in it is enumerated: the cache folders
/// are named outright, and the known user data is asserted to survive by name.</para>
/// </summary>
/// <param name="Roots">Each folder a cache folder sits in, with the cache folders it holds.</param>
/// <param name="Notes">What the user is told about the settings, including anything left alone and why.</param>
/// <param name="LeftSomethingUnexamined">
/// Whether a folder Adobe may be using was left alone or could not be placed, so a plan with nothing
/// in it must not read as clear.
/// </param>
public sealed record AdobeMediaCacheLayout(
    IReadOnlyList<DeclaredRoot> Roots,
    IReadOnlyList<PlanNote> Notes,
    bool LeftSomethingUnexamined)
{
    /// <summary>The key under <c>HKEY_CURRENT_USER</c> each release's <c>Common</c> key sits in.</summary>
    public const string SettingsKey = @"Software\Adobe";

    /// <summary>How each release's key begins: <c>Common 12.0</c>, <c>Common 13.0</c> and so on.</summary>
    public const string ReleaseKeyPrefix = "Common ";

    /// <summary>The key inside a release's key that holds the two locations.</summary>
    public const string MediaCacheKey = "Media Cache";

    /// <summary>Names the folder Adobe writes <see cref="FilesFolder"/> into.</summary>
    public const string FilesValue = "FolderPath";

    /// <summary>Names the folder Adobe writes <see cref="DatabaseFolder"/> into.</summary>
    public const string DatabaseValue = "DatabasePath";

    /// <summary>The converted audio, the waveforms and each file's importer state.</summary>
    public const string FilesFolder = "Media Cache Files";

    /// <summary>The database that indexes <see cref="FilesFolder"/>, wherever each application put it.</summary>
    public const string DatabaseFolder = "Media Cache";

    /// <summary>Waveforms, in the folder Adobe's automatic clean-up names beside <see cref="FilesFolder"/>.</summary>
    public const string PeakFolder = "Peak Files";

    /// <summary>
    /// §5.3. The four applications that share the cache, and the Dynamic Link server Premiere Pro and
    /// After Effects share footage through, which can outlive both.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames =
        ["Adobe Premiere Pro", "AfterFX", "Adobe Audition", "Adobe Media Encoder", "dynamiclinkmanager"];

    private const string CommonReason =
        "Adobe's shared folder, which also holds your LUTs, your motion graphics templates and Team "
        + "Projects' auto-saves. Deguffer removes only what is inside the media cache folders in it.";

    private const string ChosenReason =
        "The folder Adobe's settings name for its media cache. Adobe keeps its cache in a folder of its "
        + "own inside it, and Deguffer removes only what is inside that folder.";

    /// <summary>What §5.6 asserts survived in the default folder. Everything else there is simply never reached.</summary>
    private static readonly IReadOnlyList<(string RelativePath, string Reason)> CommonSurvivors =
    [
        ("Team Projects Local Hub", "Team Projects' local copies, with their auto-save history."),
        ("LUTs", "The look-up tables you installed for Premiere Pro and Media Encoder."),
        ("Motion Graphics Templates", "The motion graphics templates you installed."),
    ];

    /// <summary>
    /// <c>%APPDATA%\Adobe\Common</c>. It is also where Adobe keeps the cache until the user moves it.
    /// </summary>
    public static string DefaultFolder(IUserEnvironment environment) =>
        Path.Combine(environment.RoamingAppData, "Adobe", "Common");

    public static AdobeMediaCacheLayout Find(IUserEnvironment environment, IVolumeInventory volumes)
    {
        var notes = new List<PlanNote>();
        var withheld = false;

        var common = DefaultFolder(environment);
        var folders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // A roaming profile can put the default folder on a share, where it is withheld for the reason
        // a moved one is. Said only where something is there, so a machine without Adobe says nothing.
        if (HostVolume.For(volumes, common) is { IsLocalDisk: true })
        {
            folders[common] = [FilesFolder, PeakFolder, DatabaseFolder];
        }
        else if (LongPath.ProbeDirectory(common) is not PathPresence.Absent)
        {
            Withhold(Path.Combine(common, FilesFolder));
        }

        var releases = environment.ReadCurrentUserRegistrySubKeyNames(SettingsKey)
            .Where(name => name.StartsWith(ReleaseKeyPrefix, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var release in releases)
        {
            var key = $@"{SettingsKey}\{release}\{MediaCacheKey}";

            Setting(key, FilesValue, FilesFolder, "media cache files");
            Setting(key, DatabaseValue, DatabaseFolder, "media cache database");
        }

        return new AdobeMediaCacheLayout(
            [
                .. folders.Select(folder => new DeclaredRoot(
                    folder.Key,
                    folder.Key.Equals(common, StringComparison.OrdinalIgnoreCase) ? CommonReason : ChosenReason,
                    RequiresElevation: false,
                    [.. folder.Value.Select(name => new DeclaredLocation(name, ReasonFor(name), DeclaredLocationKind.DirectoryContents))],
                    folder.Key.Equals(common, StringComparison.OrdinalIgnoreCase) ? CommonSurvivors : [])),
            ],
            notes,
            withheld);

        // One location from one release's settings. A value that is not a full path is said out loud,
        // because Adobe may be using a folder nobody here can name.
        void Setting(string key, string value, string folder, string purpose)
        {
            var recorded = environment.ReadCurrentUserRegistryValue(key, value);

            if (string.IsNullOrWhiteSpace(recorded))
            {
                return;
            }

            if (LongPath.Configured(recorded) is not { } chosen)
            {
                withheld = true;
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Adobe's settings name '{recorded}' for its {purpose}, which is not a full path, so "
                    + "Deguffer cannot tell where Adobe puts them. Whatever is there was neither cleared nor ruled out."));

                return;
            }

            if (HostVolume.For(volumes, chosen) is not { IsLocalDisk: true })
            {
                Withhold(Path.Combine(chosen, folder));

                return;
            }

            var names = folders.TryGetValue(chosen, out var existing) ? existing : folders[chosen] = [];

            if (!names.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(folder);
            }
        }

        // Once per folder, however many releases name it.
        void Withhold(string folder)
        {
            withheld = true;

            var sentence =
                $"Left '{LongPath.Display(folder)}' alone: it is not on a disk in this computer, so an Adobe "
                + "application on another computer may be using it.";

            if (!notes.Any(note => note.Message == sentence))
            {
                notes.Add(new PlanNote(PlanNoteSeverity.Information, sentence));
            }
        }
    }

    private static string ReasonFor(string folder) => folder switch
    {
        FilesFolder =>
            "Audio Adobe converted from your footage, the waveforms it drew and what it read from each "
            + "file. Adobe makes them again the next time the footage is used.",
        PeakFolder => "Waveforms Adobe drew from your audio. Adobe draws them again when the audio is next shown.",
        _ => "Adobe's index of its media cache. Adobe builds it again as it caches your footage again.",
    };
}
