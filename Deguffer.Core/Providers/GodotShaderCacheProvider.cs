using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The shader cache inside a Godot 4 project's data folder: <c>.godot\shader_cache</c>, or
/// <c>godot\shader_cache</c> where the project turned the hidden folder off. The editor's renderer
/// keeps a compiled form of every shader variant it has used there.
///
/// <para><b>Tier 1.</b> The renderer compiles a variant it finds missing, and the editor turns the
/// cache on whatever the project says, so the cost is one slower start while the shaders compile
/// again. The editor writes the cache into the project's data folder
/// (<c>EditorPaths</c> in <see href="https://github.com/godotengine/godot/blob/master/editor/file_system/editor_paths.cpp">editor_paths.cpp</see>,
/// and <see href="https://github.com/godotengine/godot/blob/master/servers/rendering/renderer_rd/renderer_compositor_rd.cpp">renderer_compositor_rd.cpp</see>).</para>
///
/// <para>Its own row because the import cache beside it costs minutes to restore, and a user can take
/// this one alone. <see cref="GodotImportCacheProvider"/> records why an open editor is found by the
/// live-tree veto, and <see cref="GodotDataFolder"/> why nothing else in the data folder is offered.</para>
/// </summary>
public sealed class GodotShaderCacheProvider : BuildDirectoryProvider
{
    private readonly GodotProjectSurvey _projects;

    public GodotShaderCacheProvider(
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

    /// <summary>Recognition shared with the import cache row, and this row's half of the data folder.</summary>
    private static BuildDirectoryKind KindOver(GodotProjectSurvey projects) => GodotDataFolder.Recognition(projects) with
    {
        Parts = (folder, ct) => projects.DataFolder(folder, ct).ShaderCache(ct),
    };

    public override string Id => "godot-shader-cache";

    public override string Name => "Godot project shader caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The next time you open each project, the Godot editor compiles its shaders again, so it starts "
        + "more slowly once. Nothing is lost.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "the Godot editor",
        Publisher = "the Godot Engine project",
        Purpose = "The Godot editor keeps the shaders it has compiled for a project in the "
            + "shader_cache folder inside the project's .godot folder, so it does not compile them "
            + "again each time the project opens.",
        Recommendation = "Removing it costs one slower start. Deguffer removes only the shader_cache "
            + "folder, only where every file in it is one Godot's renderer wrote, and never the rest "
            + "of the .godot folder.",
    };

    protected override string Subject => "Godot shader cache";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for "
        + "Godot projects inside them, and nowhere else.";
}
