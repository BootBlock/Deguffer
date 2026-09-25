using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What makes a folder an Unreal project, for the providers that remove build output from one.
///
/// <para><b>The evidence is the project's own descriptor.</b> Every Unreal project has a
/// <c>&lt;Name&gt;.uproject</c> file at its top, and the editor opens a project through it. A plugin
/// has a <c>.uplugin</c> instead and the engine has neither, so an <c>Intermediate</c> inside a
/// plugin or an engine checkout is never recognised. Those folders hold build output too, but
/// nothing here has established what else they hold, and §5.2 leaves the unrecognised alone.</para>
///
/// <para><b><c>Saved</c> is never a target, and is named as a survivor.</b> It holds
/// <c>Saved\Autosaves</c>, which is the only copy of editor work nobody saved, and
/// <c>Saved\Config</c>, which is the user's own editor settings.</para>
///
/// <para><b><c>Binaries</c> is never a target either</b>, although it is compiled output. A
/// Blueprint-only project has little in it, and a C++ project rebuilds it from <c>Source</c> — but
/// only where a compiler is installed. On a team, an artist often receives the compiled game module
/// from source control and cannot build it, and on that machine the folder is the only copy there
/// is. Nothing on the disk tells the two machines apart, so the folder is left alone.</para>
/// </summary>
internal static class UnrealProjectLayout
{
    /// <summary>
    /// The Unreal processes that write a project's build output and derived data: the editor and its
    /// command-line form, which cooks and runs commandlets, in their Unreal Engine 5 and 4 names, and
    /// Unreal Build Tool. Each works in the engine's folder rather than the project's, so the
    /// providers warn by name as well as asking the veto.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames =
        ["UnrealEditor", "UnrealEditor-Cmd", "UE4Editor", "UE4Editor-Cmd", "UnrealBuildTool"];

    /// <summary>A build directory of this name, recognised by the descriptor beside it.</summary>
    public static BuildDirectoryKind Kind(string directoryName) => new()
    {
        DirectoryNames = [directoryName],
        RequiredSiblingExtensions = [".uproject"],

        // What a rule reaching one folder too far would take. The other provider's own directory is
        // not named: one run can remove both, and each is the other's target rather than a
        // survivor of it.
        ProtectedSiblings = ["Saved", "Config", "Content", "Source", "Plugins", "Binaries"],
        ProjectLockFiles = LiveLogs,
    };

    /// <summary>
    /// The logs in the project's <c>Saved\Logs</c> an editor or commandlet may be writing.
    ///
    /// <para>Each holds its log open from start-up until it exits, sharing it for reading only, so a
    /// log that is held is an Unreal process with this project open. The engine's source says so
    /// for 5.3, 5.5 and 5.8. The name is not predicted: it is the project's by default, a second
    /// instance adds <c>_2</c>, and a switch on the command line can choose another. So every log
    /// there is asked about, and Windows says which are held. The copies the engine makes of an old
    /// log on start-up are never held, and are left out.</para>
    ///
    /// <para>A log moved out of the project with <c>-ABSLOG</c>, or none at all, leaves only the
    /// warning by process name, which the providers give as well.</para>
    /// </summary>
    private static IReadOnlyList<string> LiveLogs(string project)
    {
        try
        {
            return
            [
                .. new DirectoryInfo(LongPath.Extended(Path.Combine(project, "Saved", "Logs")))
                    .EnumerateFiles()
                    .Where(file => file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
                        && !file.Name.Contains("-backup-", StringComparison.OrdinalIgnoreCase))
                    .Select(file => Path.Combine(project, "Saved", "Logs", file.Name)),
            ];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // No logs folder, or one Windows will not list: nothing to ask about, and the process
            // table and the warning still apply.
            return [];
        }
    }
}
