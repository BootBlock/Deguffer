using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What PlatformIO's core directory holds, as the PlatformIO row declares it for §7.1 and as §5.6
/// asserts it survived.
/// </summary>
internal static class PlatformIoCoreLayout
{
    /// <summary>
    /// §5.2 over one core directory. <see cref="PlatformIoCacheProvider.ToolRoots"/> and
    /// <see cref="PlatformIoCacheProvider.DiscoverToolRootsAsync"/> differ only in which directory
    /// they are given.
    /// </summary>
    public static IReadOnlyList<ToolRoot> Declare(string core) =>
    [
        ToolRoot.Folders(
            core,
            "This is PlatformIO's own folder. Deguffer clears the download cache inside it, and "
            + "removes an installed package only where PlatformIO itself reports that nothing needs "
            + "it, because the toolchains, the Python that PlatformIO runs on and your global "
            + "libraries all sit beside that cache.",
            static name => name.Equals(".cache", StringComparison.OrdinalIgnoreCase)),
    ];

    /// <summary>
    /// The packages directory PlatformIO reports, as a root of its own. It recognises no child at all
    /// because it is never a target, which is the same rule the core root's predicate states.
    /// </summary>
    public static ToolRoot PackagesRoot(string packages) => new(
        packages,
        "These are the toolchains, frameworks and platforms PlatformIO installed. Deguffer "
        + "removes one only where PlatformIO itself reports that no project needs it, "
        + "because nothing about the folder says which of your boards still builds.",
        static _ => false);

    /// <summary>
    /// §5.6. The siblings are the whole point here: <c>packages</c> and <c>platforms</c> are the
    /// installed toolchains that make up nearly all of the core directory's size, <c>penv</c> and
    /// <c>python3</c> are the interpreter PlatformIO itself runs on, and <c>lib</c> holds globally
    /// installed user libraries that were never a cache at all.
    ///
    /// <para><c>packages</c> stays on this list now that a step may clear part of it, and asserting
    /// its survival is no weaker a claim than before. Neither prune category removes the directory:
    /// each uninstalls individual packages inside it, and a current PlatformIO's own core packages
    /// are never candidates. A run that left <c>packages</c> missing would mean something removed
    /// the folder itself, which is precisely what §5.6 exists to catch.</para>
    ///
    /// <para>Built from the directory PlatformIO reported rather than from
    /// <see cref="PlatformIoCacheProvider.CoreRoot"/>. A relocated core directory would otherwise
    /// leave every one of these paths absent, and <see cref="Execution.ProtectedPath.PresenceBefore"/>
    /// would record them as absent — six assertions that pass without establishing anything, on
    /// exactly the installs where the guess about where PlatformIO lives has already been shown to be
    /// wrong.</para>
    /// </summary>
    public static IReadOnlyList<(string Path, string Reason)> Survivors(PlatformIoLocations locations)
    {
        var core = locations.CoreDirectory;

        return
        [
            (core, "PlatformIO's core directory must survive — only the cache and the unused packages within it are cleared."),
            (locations.PackagesDirectory, "Installed toolchains and frameworks; PlatformIO removes individual unused packages, never the folder."),
            (Path.Combine(core, "platforms"), "Installed development platform definitions."),
            (Path.Combine(core, "penv"), "The virtual environment PlatformIO Core itself runs in."),
            (Path.Combine(core, "python3"), "The bundled Python interpreter backing that environment."),
            (Path.Combine(core, "lib"), "Globally installed user libraries — never a cache."),
        ];
    }
}
