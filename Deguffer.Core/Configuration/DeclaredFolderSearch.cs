using Deguffer.Core.Providers;

namespace Deguffer.Core.Configuration;

/// <summary>
/// What Deguffer looks for inside the folders a user declares on Settings, named as the Storage page
/// names each location, so the reader deciding whether to add a folder sees the list it widens.
///
/// <para>A list built from what is supported rather than a sentence of names written by hand,
/// because a newly supported tool goes unmentioned in a sentence nobody thinks to reread.</para>
/// </summary>
public static class DeclaredFolderSearch
{
    /// <summary>
    /// The locations read only inside the source folders, in the order the Settings list shows them.
    /// Written out because a provider's name exists only on an instance, and building every provider
    /// to read it is a whole planner's work for one list. <c>DeclaredFolderSearchTests</c> holds it to
    /// the providers that wait for a source folder, so a provider added there fails until it is here.
    /// </summary>
    public static IReadOnlyList<string> InSourceFolders { get; } =
    [
        ".NET intermediate build output",
        "Dart and Flutter tool folders",
        "Git LFS old file versions",
        "Godot 3 project import caches",
        "Godot project import caches",
        "Godot project shader caches",
        "mypy caches",
        "Node.js project dependencies",
        "nox environments",
        "Nx workspace task caches",
        "pytest caches",
        "Python virtual environments",
        "ruff caches",
        "Rust build output",
        "Rust incremental compilation caches",
        "tox environments",
        "Unity project library",
        "Unreal project derived data cache",
        "Unreal project intermediate files",
        "Visual Studio solution caches",
    ];

    /// <summary>
    /// The emulators Deguffer knows, whose portable installs an emulator folder declares. Read from the
    /// layouts themselves, so an emulator supported is an emulator listed. RetroArch is found by its
    /// program rather than by a layout (see <see cref="RetroArchDiscovery"/>), so it is named here.
    /// In alphabetical order, the order a reader looking for one emulator scans in.
    /// </summary>
    public static IReadOnlyList<string> Emulators { get; } =
    [
        .. EmulatorShaderCacheProvider.Layouts.Select(layout => layout.Name)
            .Append("RetroArch")
            .Order(StringComparer.OrdinalIgnoreCase),
    ];
}
