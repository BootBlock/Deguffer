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
    /// The Unreal editor, in its Unreal Engine 5 and Unreal Engine 4 names. The editor works in the
    /// engine's folder rather than the project's, so the veto on a project in use cannot see it, and
    /// the providers warn by name instead.
    /// </summary>
    public static readonly IReadOnlyList<string> EditorProcessNames = ["UnrealEditor", "UE4Editor"];

    /// <summary>A build directory of this name, recognised by the descriptor beside it.</summary>
    public static BuildDirectoryKind Kind(string directoryName) => new()
    {
        DirectoryNames = [directoryName],
        RequiredSiblingExtensions = [".uproject"],

        // What a rule reaching one folder too far would take. The other provider's own directory is
        // not named: one run can remove both, and each is the other's target rather than a
        // survivor of it.
        ProtectedSiblings = ["Saved", "Config", "Content", "Source", "Plugins", "Binaries"],
    };
}
