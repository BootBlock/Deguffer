using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The import cache inside a Godot 4 project's data folder: <c>.godot\imported</c>, or
/// <c>godot\imported</c> where the project turned the hidden folder off. The editor keeps the
/// imported form of every asset there, such as compressed textures and converted scenes.
///
/// <para><b>Tier 2.</b> Nothing in it is anybody's only copy. The editor's scan finds the files gone
/// and imports the assets again, because <c>editor/import/reimport_missing_imported_files</c> is true
/// by default (<see href="https://github.com/godotengine/godot/blob/master/editor/file_system/editor_file_system.cpp">editor_file_system.cpp</see>),
/// and Godot's documentation says an erased imported resource "will be reimported automatically". The
/// price is that import: on a large project it takes minutes. No command clears the cache, and
/// <c>godot --headless --import</c> rebuilds it, which is a recovery and not an eviction (§5.1).</para>
///
/// <para><b>The data folder itself is never a target.</b> <see cref="GodotDataFolder"/> says what else
/// it holds and why, and it is shared with <see cref="GodotShaderCacheProvider"/>, which offers the
/// shader cache beside this one on its own row.</para>
///
/// <para><b>An editor with the project open holds nothing open here</b>, so a delete would not fail on
/// a lock. It would leave the editor's resources pointing at files that are gone. The editor is found
/// by where it works instead: it changes its working directory to the project it opens, whether it is
/// given <c>--path</c> or a <c>project.godot</c>, and the project manager opens a project by
/// starting the editor with <c>--path</c> (<c>Main::setup</c> in
/// <see href="https://github.com/godotengine/godot/blob/master/main/main.cpp">main.cpp</see>,
/// <see href="https://github.com/godotengine/godot/blob/master/editor/project_manager/project_manager.cpp">project_manager.cpp</see>).
/// So the live-tree veto, which refuses a project a program is working in, refuses a project an editor
/// has open. A game the editor runs is started with <c>--path</c> too
/// (<see href="https://github.com/godotengine/godot/blob/master/editor/run/editor_run.cpp">editor_run.cpp</see>).</para>
/// </summary>
public sealed class GodotImportCacheProvider : BuildDirectoryProvider
{
    private readonly GodotProjectSurvey _projects;

    public GodotImportCacheProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null,
        GodotProjectSurvey? projects = null)
        : base(KindOver(projects ??= new GodotProjectSurvey()), roots, discovery, liveTrees, environment, runner, inspector, scanner, volumes)
    {
        _projects = projects;
    }

    public override void InvalidateCaches()
    {
        base.InvalidateCaches();
        _projects.Invalidate();
    }

    /// <summary>
    /// Recognition shared with the shader cache row, and this row's half of the data folder. An
    /// unshared survey is correct and reads each project for this row alone, which is what a test
    /// wants; the planner hands every Godot row one.
    /// </summary>
    private static BuildDirectoryKind KindOver(GodotProjectSurvey projects) => GodotDataFolder.Recognition(projects) with
    {
        Parts = (folder, ct) => projects.DataFolder(folder, ct).ImportCache(ct),
    };

    public override string Id => "godot-imported";

    public override string Name => "Godot project import caches";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next time you open each project, the Godot editor imports all of its assets again. On a "
        + "large project that takes minutes. Nothing is lost: all of it is made from the project's own "
        + "assets and their import settings, which stay.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "the Godot editor",
        Publisher = "the Godot Engine project",
        Purpose = "Godot converts every asset in a project, such as a texture or a 3D model, into a form "
            + "the engine loads quickly, and keeps the result in the imported folder inside the "
            + "project's .godot folder.",
        Recommendation = "Godot imports everything again by itself, but on a large project that takes "
            + "minutes. Deguffer removes only the imported folder, only where every file in it is one "
            + "Godot's importer wrote, and never the rest of the .godot folder, which holds the "
            + "project's export passwords and the editor's state.",
    };

    protected override string Subject => "Godot imported assets";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for "
        + "Godot projects inside them, and nowhere else.";
}
