using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Microsoft.Win32;

namespace Deguffer.Core.Providers;

/// <summary>
/// Finds Jellyfin's transcoder folder, from the installer's record of the data folder and the server's
/// own settings inside it.
///
/// <para><b>The data folder is the user's choice, and the installer records it.</b> Jellyfin's
/// installer writes <c>DataFolder</c> under <c>HKLM\SOFTWARE\Jellyfin\Server</c>, in the 32-bit view
/// because the installer is a 32-bit program. Its two defaults are looked at as well: the one the
/// server uses when it is run without the installer, and the one a service install uses.</para>
///
/// <para><b>Two settings move the folder, and a moved one is used as it is named.</b> The cache
/// folder comes from <c>CachePath</c> in <c>config\system.xml</c>, then from the
/// <c>JELLYFIN_CACHE_DIR</c> variable, then is <c>cache</c> in the data folder. The transcoder writes
/// to <c>transcodes</c> in the cache folder, unless <c>TranscodingTempPath</c> in
/// <c>config\encoding.xml</c> names a folder, which it then writes straight into.</para>
///
/// <para><b>So the folder has to prove it is Jellyfin's.</b> The server writes a
/// <c>.jellyfin-transcode</c> file into whichever folder it transcodes to, and refuses to start if a
/// folder carries another server folder's marker. That file is the evidence this provider requires.
/// A default folder is also accepted where the cache folder above it carries the
/// <c>CACHEDIR.TAG</c> Jellyfin writes there, which is how a server older than the marker is
/// recognised. A folder with neither is left alone and named.</para>
///
/// <para><b>A marker outlives the server that wrote it.</b> A folder the user once pointed Jellyfin at
/// may be one they use again after the server has gone, so a moved folder is trusted only through the
/// settings of the data folder the installer still records. A transcoder folder that holds a data
/// folder, or overlaps what a data folder keeps, is never offered: emptying it would take the
/// server's own data.</para>
///
/// <para>Jellyfin empties the whole folder itself each time it starts, marker included, and writes the
/// marker again when it next transcodes.</para>
/// </summary>
public static class JellyfinServerLayout
{
    /// <summary>The installer's key under <c>HKEY_LOCAL_MACHINE</c>, in the 32-bit view.</summary>
    public const string RegistryKey = @"Software\Jellyfin\Server";

    public const string DataFolderValue = "DataFolder";

    /// <summary><c>NetworkService</c> or <c>LocalSystem</c> for a service install, <c>None</c> otherwise.</summary>
    public const string ServiceAccountValue = "ServiceAccountType";

    public const string CacheVariable = "JELLYFIN_CACHE_DIR";

    public const string Marker = ".jellyfin-transcode";

    public const string CacheTag = "CACHEDIR.TAG";

    /// <summary>§5.3. The server, and the tray program that starts it.</summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["jellyfin", "Jellyfin.Windows.Tray"];

    private const string TranscodeReason =
        "Parts of films and episodes Jellyfin converted for a player that could not play the original. "
        + "Jellyfin converts them again when they are next played.";

    private const string ParentReason =
        "The folder that holds Jellyfin's transcoder folder. Deguffer removes only what is inside the "
        + "transcoder folder.";

    private const string DataReason =
        "Jellyfin's own folder, which holds its settings, its database and its backups, your libraries "
        + "and the artwork it gathered. Deguffer removes only the transcoder's leftovers.";

    private const string ExploreReason =
        "This is Jellyfin's own folder. Its transcoder's leftovers are removed from the Storage page, "
        + "which leaves a film playing now alone, and nothing else here is a cache.";

    private const string TranscodeExploreReason =
        "This is the folder Jellyfin's settings name for its transcoder. Its leftovers are removed from "
        + "the Storage page, which leaves a film playing now alone.";

    /// <summary>What §5.6 asserts survived in every data folder, as relative path and reason.</summary>
    private static readonly (string RelativePath, string Reason)[] DataNames =
    [
        ("config", "Jellyfin's settings, including where it keeps everything else."),
        ("data", "Jellyfin's database: your libraries, users and watch history."),
        (@"data\backups", "The backups Jellyfin made of its own database and settings."),
        ("metadata", "The artwork and details Jellyfin gathered for your libraries."),
        ("plugins", "The plugins you installed, and their settings."),
        ("root", "How your libraries are defined."),
    ];

    public static MediaServerLayout Find(IUserEnvironment environment, ISystemDirectories system)
    {
        var recorded = LongPath.Configured(
            environment.ReadLocalMachineRegistryValue(RegistryKey, DataFolderValue, RegistryView.Registry32));
        var serviceAccount = environment.ReadLocalMachineRegistryValue(
            RegistryKey, ServiceAccountValue, RegistryView.Registry32);

        string[] candidates =
        [
            .. recorded is null ? [] : new[] { recorded },
            Path.Combine(environment.LocalAppData, "jellyfin"),
            Path.Combine(system.ProgramData, "Jellyfin", "Server"),
        ];

        var roots = new List<DeclaredRoot>();
        var survivors = new List<(string Path, string Reason)>();
        var notes = new List<PlanNote>();
        var toolRoots = new List<ToolRoot>();
        var withheld = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var present = new List<string>();

        foreach (var data in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            switch (LongPath.ProbeDirectory(data))
            {
                case PathPresence.Present:
                    present.Add(data);
                    break;

                // Declared at its default rather than read, so the scan names the folder Windows would
                // not describe instead of this reporting settings it could not have read.
                case PathPresence.Refused:
                    roots.Add(new DeclaredRoot(
                        data,
                        DataReason,
                        RequiresElevation: false,
                        [new DeclaredLocation(Path.Combine("cache", "transcodes"), TranscodeReason, DeclaredLocationKind.DirectoryContents)],
                        []));
                    break;
            }
        }

        foreach (var data in present)
        {
            toolRoots.Add(ToolRoot.Of(data, ExploreReason, new DisposableChildSet([])));
            survivors.Add((data, DataReason));
            survivors.AddRange(DataNames.Select(name => (Path.Combine(data, name.RelativePath), name.Reason)));

            var isRecorded = data.Equals(recorded, StringComparison.OrdinalIgnoreCase);

            // The service account owns what a service install writes, so this account may not remove it.
            var elevated = isRecorded
                && serviceAccount is { Length: > 0 } account
                && !account.Equals("None", StringComparison.OrdinalIgnoreCase);

            var config = Path.Combine(data, "config");
            var cacheSetting = MediaServerSetting.Read(Path.Combine(config, "system.xml"), "CachePath");
            var transcodeSetting = MediaServerSetting.Read(Path.Combine(config, "encoding.xml"), "TranscodingTempPath");

            var cache = cacheSetting.Folder
                ?? LongPath.Configured(environment.GetEnvironmentVariable(CacheVariable))
                ?? Path.Combine(data, "cache");
            var fallback = Path.Combine(cache, "transcodes");

            foreach (var setting in new[] { cacheSetting, transcodeSetting }.Where(s => s.Reading is MediaServerSettingReading.Unknown))
            {
                withheld = true;
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Deguffer could not read '{setting.File}', or it names a folder that is not a full path, "
                    + "so it cannot tell whether Jellyfin transcodes somewhere else. Anything there was "
                    + "neither cleared nor ruled out."));
            }

            // The default folder is looked at even where the setting moves the transcoder, because the
            // server left its segments there until the setting was changed.
            foreach (var folder in new[] { transcodeSetting.Folder, fallback }.OfType<string>())
            {
                if (!seen.Add(folder))
                {
                    continue;
                }

                var presence = LongPath.ProbeDirectory(folder);

                if (presence is PathPresence.Absent)
                {
                    continue;
                }

                var isMoved = !folder.Equals(fallback, StringComparison.OrdinalIgnoreCase);

                if (isMoved)
                {
                    toolRoots.AddRange(MediaServerLayout.Refusing(folder, TranscodeExploreReason));
                }

                if (Path.GetDirectoryName(folder) is not { } parent)
                {
                    Withhold(folder, "it is the whole of a drive");
                    continue;
                }

                if (Why(folder, presence, isMoved) is { } why)
                {
                    Withhold(folder, why);
                    continue;
                }

                roots.Add(new DeclaredRoot(
                    parent,
                    ParentReason,
                    elevated,
                    [new DeclaredLocation(Path.GetFileName(folder), TranscodeReason, DeclaredLocationKind.DirectoryContents)],
                    []));
            }

            // Why a transcoder folder is not offered, or null where it is. A folder Windows would not
            // describe is offered all the same, so the scan names it rather than this guessing at it.
            string? Why(string folder, PathPresence presence, bool isMoved)
            {
                // A data folder inside a transcoder folder would be emptied with it, and so would what a
                // data folder keeps where the two overlap. Jellyfin refuses to start that way, so only
                // settings nothing runs can say so. The default folder sits inside the data folder's
                // cache, which is why the data folder itself is asked about in one direction only.
                if (present.Any(other => LongPath.Contains(folder, other))
                    || present.SelectMany(other => DataNames, (other, name) => Path.Combine(other, name.RelativePath))
                        .Any(kept => LongPath.Contains(folder, kept) || LongPath.Contains(kept, folder)))
                {
                    return "it overlaps a folder Jellyfin keeps its own data in";
                }

                // A moved folder's marker outlives the server that wrote it, and a folder the user once
                // pointed Jellyfin at may be one they use again. Only the settings of the install the
                // installer still records are trusted to say the folder is still Jellyfin's.
                if (isMoved && !isRecorded)
                {
                    return "the settings that name it belong to a Jellyfin the installer no longer records";
                }

                return presence is PathPresence.Present && !IsJellyfins(folder, fallback, cache)
                    ? $"it has no {Marker} file in it"
                    : null;
            }
        }

        return new MediaServerLayout(roots, survivors, notes, toolRoots, withheld);

        void Withhold(string folder, string why)
        {
            withheld = true;
            survivors.Add((folder, $"Left alone because {why}."));
            notes.Add(Unrecognised(folder, why));
        }
    }

    private static bool IsJellyfins(string folder, string fallback, string cache) =>
        LongPath.FileExists(Path.Combine(folder, Marker))
        || (folder.Equals(fallback, StringComparison.OrdinalIgnoreCase) && LongPath.FileExists(Path.Combine(cache, CacheTag)));

    private static PlanNote Unrecognised(string folder, string why) => new(
        PlanNoteSeverity.Information,
        $"'{folder}' is where Jellyfin would transcode to, but {why}, so Deguffer left it alone.");
}
