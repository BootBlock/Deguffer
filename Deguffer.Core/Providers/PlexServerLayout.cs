using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Finds Plex Media Server's transcoder files, from the settings Plex keeps in the registry.
///
/// <para><b>Plex on Windows keeps its settings in the registry, not in a file.</b> Three of them move
/// folders this provider cares about, and each is read rather than assumed:</para>
/// <list type="bullet">
/// <item><c>LocalAppDataPath</c> moves the data folder. Plex's folder is <c>Plex Media Server</c>
/// inside the one it names, exactly as it is inside <c>%LOCALAPPDATA%</c> by default.</item>
/// <item><c>TranscoderTempDirectory</c> moves the transcoder's working folder. Plex writes into
/// <c>Transcode\Sessions</c> inside the folder it names, never into that folder itself.</item>
/// <item><c>DownloadsTempDirectory</c> is where Plex prepares downloads for its apps. It is not a
/// cache, so nothing in it is offered, and a folder of Plex's that overlaps it is withheld.</item>
/// </list>
///
/// <para><b>§5.2, and Plex is the server where it matters most.</b> Only <c>Transcode\Sessions</c>
/// and <c>PhotoTranscoder</c> are recognised. <c>Transcode\Sync</c>, beside <c>Sessions</c>, holds
/// converted media still waiting to go to a phone. <c>Plug-in Support\Databases</c> holds the watch
/// history and ratings. <c>Metadata</c> and <c>Media</c> took hours of processor time to build. Each
/// is asserted to survive by name.</para>
/// </summary>
public static class PlexServerLayout
{
    /// <summary>Plex's own key under <c>HKEY_CURRENT_USER</c>.</summary>
    public const string RegistryKey = @"Software\Plex, Inc.\Plex Media Server";

    public const string DataFolderValue = "LocalAppDataPath";

    public const string TranscoderValue = "TranscoderTempDirectory";

    public const string DownloadsValue = "DownloadsTempDirectory";

    public const string FolderName = "Plex Media Server";

    /// <summary>Where the transcoder writes, below the data folder's <c>Cache</c> or below a moved folder.</summary>
    public const string Sessions = @"Transcode\Sessions";

    /// <summary>§5.3. The server, and the transcoder it starts for each stream.</summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["Plex Media Server", "Plex Transcoder"];

    private const string DataReason =
        "Plex Media Server's own folder, which holds its database, your watch history and the "
        + "artwork and details it gathered. Deguffer removes only the transcoder's leftovers inside it.";

    private const string SessionsReason =
        "Parts of films and episodes Plex converted for a player that could not play the original. "
        + "Plex converts them again when they are next played.";

    private const string PhotoReason =
        "Pictures Plex resized for its apps, such as posters and thumbnails. Plex makes them again when "
        + "they are next shown.";

    private const string MovedReason =
        "The folder Plex's settings name for its transcoder. Deguffer removes only the parts of films "
        + "Plex left in Transcode\\Sessions inside it.";

    private const string DownloadsReason =
        "The folder Plex's settings name for preparing downloads for its apps. Nothing in it is a "
        + "cache, and Deguffer removes nothing in it.";

    private const string ExploreReason =
        "This is Plex Media Server's own folder. Its transcoder's leftovers are removed from the "
        + "Storage page, which leaves a film playing now alone, and nothing else here is a cache.";

    public static MediaServerLayout Find(IUserEnvironment environment)
    {
        var notes = new List<PlanNote>();
        var withheld = false;

        var data = Path.Combine(Setting(DataFolderValue, "its data").Folder ?? environment.LocalAppData, FolderName);
        var transcoder = Setting(TranscoderValue, "its transcoder");
        var downloads = Setting(DownloadsValue, "preparing downloads");

        var survivors = new List<(string Path, string Reason)>();
        var toolRoots = new List<ToolRoot> { ToolRoot.Of(data, ExploreReason, new DisposableChildSet([])) };

        var defaultSessions = Path.Combine(data, "Cache", Sessions);

        var dataLocations = new List<DeclaredLocation>();

        if (Offers(defaultSessions))
        {
            dataLocations.Add(new(Path.Combine("Cache", Sessions), SessionsReason, DeclaredLocationKind.DirectoryContents));
        }

        if (Offers(Path.Combine(data, "Cache", "PhotoTranscoder")))
        {
            dataLocations.Add(new(Path.Combine("Cache", "PhotoTranscoder"), PhotoReason, DeclaredLocationKind.DirectoryContents));
        }

        var roots = new List<DeclaredRoot>
        {
            new(data, DataReason, RequiresElevation: false, dataLocations,
            [
                (@"Plug-in Support\Databases", "Plex's database: your libraries, watch history and ratings."),
                ("Metadata", "The artwork and details Plex gathered for your libraries, which take hours to gather again."),
                ("Media", "What Plex worked out from your media files, such as chapter pictures and preview thumbnails."),
                (@"Cache\Transcode\Sync", "Media Plex converted and is still waiting to send to a phone or tablet."),
            ]),
        };

        if (transcoder.Folder is { } moved)
        {
            var movedSessions = Path.Combine(moved, Sessions);

            toolRoots.AddRange(MediaServerLayout.Refusing(Path.Combine(moved, "Transcode"), MovedReason));

            if (!movedSessions.Equals(defaultSessions, StringComparison.OrdinalIgnoreCase) && Offers(movedSessions))
            {
                roots.Add(new DeclaredRoot(
                    moved,
                    MovedReason,
                    RequiresElevation: false,
                    [new DeclaredLocation(Sessions, SessionsReason, DeclaredLocationKind.DirectoryContents)],
                    [(@"Transcode\Sync", "Media Plex converted and is still waiting to send to a phone or tablet.")]));
            }
        }

        if (downloads.Folder is { } prepared)
        {
            survivors.Add((prepared, DownloadsReason));
            toolRoots.AddRange(MediaServerLayout.Refusing(prepared, DownloadsReason));
        }

        return new MediaServerLayout(roots, survivors, notes, toolRoots, withheld);

        // A folder is withheld wherever it and the downloads folder overlap, in either direction:
        // neither setting says what Plex does inside the other, and a download waiting to go to a phone
        // is not a cache.
        //
        // A downloads setting that names no full path could be anywhere, so every folder is withheld
        // then: the overlap cannot be ruled out.
        bool Offers(string folder)
        {
            if (!downloads.Unplaced && (downloads.Folder is not { } downloadFolder
                || !(LongPath.Contains(downloadFolder, folder) || LongPath.Contains(folder, downloadFolder))))
            {
                return true;
            }

            if (LongPath.ProbeDirectory(folder) is not PathPresence.Absent)
            {
                withheld = true;
                survivors.Add((folder, "Left alone because it may overlap the folder Plex prepares downloads in."));
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"'{folder}' may overlap the folder Plex prepares downloads in. A download waiting to go "
                    + "to a phone may be in there, so Deguffer left the folder alone."));
            }

            return false;
        }

        // One of Plex's folder settings. A value that is not a full path is said out loud, because Plex
        // may be using a folder nobody here can name.
        (string? Folder, bool Unplaced) Setting(string value, string purpose)
        {
            var recorded = environment.ReadCurrentUserRegistryValue(RegistryKey, value);

            if (string.IsNullOrWhiteSpace(recorded))
            {
                return (null, false);
            }

            if (LongPath.Configured(recorded) is { } folder)
            {
                return (folder, false);
            }

            withheld = true;
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Plex's settings name '{recorded}' for {purpose}, which is not a full path, so Deguffer "
                + "cannot tell where Plex puts it. Whatever is there was neither cleared nor ruled out."));

            return (null, true);
        }
    }
}
