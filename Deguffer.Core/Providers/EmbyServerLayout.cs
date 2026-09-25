using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Finds Emby Server's transcoder folder, from the server's own settings in its program data folder.
///
/// <para><b>The folder's name is fixed, and that is what makes a moved one safe to name.</b> Emby
/// keeps its program data in <c>%APPDATA%\Emby-Server\programdata</c>, and transcodes to
/// <c>transcoding-temp</c> inside it. <c>TranscodingTempPath</c> in <c>config\encoding.xml</c> moves
/// the transcoder, and Emby then writes to <c>transcoding-temp</c> inside the folder it names, never
/// into that folder itself. So the only folder ever emptied is one called <c>transcoding-temp</c> in a
/// folder Emby's settings name. Emby's help warns that it deletes everything in the folder it
/// transcodes to, which is the reason the folder the setting names is never a target.</para>
///
/// <para>The default folder is looked at even where the setting moves the transcoder. Emby left its
/// segments there until the setting changed, and goes back to it when it cannot write to the folder
/// the setting names.</para>
/// </summary>
public static class EmbyServerLayout
{
    public const string TranscodeFolderName = "transcoding-temp";

    /// <summary>§5.3. The server, which runs its transcoder as a separate process of its own.</summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["EmbyServer"];

    private const string TranscodeReason =
        "Parts of films and episodes Emby converted for a player that could not play the original. "
        + "Emby converts them again when they are next played.";

    private const string DataReason =
        "Emby Server's own folder, which holds its settings, its database, your libraries and the "
        + "artwork it gathered. Deguffer removes only the transcoder's leftovers inside it.";

    private const string MovedReason =
        "The folder Emby's settings name for its transcoder. Deguffer removes only what is inside the "
        + "transcoding-temp folder Emby made in it.";

    private const string ExploreReason =
        "This is Emby Server's own folder. Its transcoder's leftovers are removed from the Storage page, "
        + "which leaves a film playing now alone, and nothing else here is a cache.";

    private const string TranscodeExploreReason =
        "This is Emby's transcoder folder. Its leftovers are removed from the Storage page, which leaves "
        + "a film playing now alone.";

    /// <summary>Emby's program data folder in this profile.</summary>
    public static string ProgramData(IUserEnvironment environment) =>
        Path.Combine(environment.RoamingAppData, "Emby-Server", "programdata");

    public static MediaServerLayout Find(IUserEnvironment environment)
    {
        var data = ProgramData(environment);
        var setting = MediaServerSetting.Read(Path.Combine(data, "config", "encoding.xml"), "TranscodingTempPath");

        var notes = new List<PlanNote>();
        var toolRoots = new List<ToolRoot> { ToolRoot.Of(data, ExploreReason, new DisposableChildSet([])) };

        var roots = new List<DeclaredRoot>
        {
            new(
                data,
                DataReason,
                RequiresElevation: false,
                [new DeclaredLocation(TranscodeFolderName, TranscodeReason, DeclaredLocationKind.DirectoryContents)],
                [
                    ("config", "Emby's settings, including where it keeps everything else."),
                    ("data", "Emby's database: your libraries, users and watch history."),
                    ("metadata", "The artwork and details Emby gathered for your libraries."),
                    ("plugins", "The plugins you installed, and their settings."),
                    ("root", "How your libraries are defined."),
                ]),
        };

        if (setting.Folder is { } moved
            && !moved.Equals(data, StringComparison.OrdinalIgnoreCase))
        {
            roots.Add(new DeclaredRoot(
                moved,
                MovedReason,
                RequiresElevation: false,
                [new DeclaredLocation(TranscodeFolderName, TranscodeReason, DeclaredLocationKind.DirectoryContents)],
                []));
            toolRoots.AddRange(MediaServerLayout.Refusing(Path.Combine(moved, TranscodeFolderName), TranscodeExploreReason));
        }
        else if (setting.Reading is MediaServerSettingReading.Unknown)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Deguffer could not read '{setting.File}', or it names a folder that is not a full path, so "
                + "it cannot tell whether Emby transcodes somewhere else. Anything there was neither "
                + "cleared nor ruled out."));
        }

        return new MediaServerLayout(roots, [], notes, toolRoots, LeftSomethingUnexamined: notes.Count > 0);
    }
}
