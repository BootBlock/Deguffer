using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A Godot 3 project's import cache: the <c>.import</c> folder beside its <c>project.godot</c>, or
/// <c>import</c> where the project turned the hidden folder off.
///
/// <para><b>Removed whole, unlike Godot 4's.</b> Godot 3 kept nothing in its data folder but the
/// imported files, their <c>.md5</c> records and a <c>.gdignore</c>; the editor's state and export
/// settings lived elsewhere. So the folder is offered whole, and only where every entry in it is one
/// of those (<see cref="GodotCacheContents"/>) and the project's <c>config_version</c> says Godot 3
/// wrote it (<see cref="GodotProjectSettings"/>). A Godot 4 project has its own data folder, and a
/// <c>.import</c> left in one by an upgrade is not offered.</para>
///
/// <para><b>Tier 2</b>, for the reason <see cref="GodotImportCacheProvider"/> gives, which also records
/// why an open editor is found by the live-tree veto. Godot 3's editor changes its working directory to
/// the project in the same way.</para>
/// </summary>
public sealed class Godot3ImportCacheProvider : BuildDirectoryProvider
{
    private readonly GodotProjectSurvey _projects;

    public Godot3ImportCacheProvider(
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
    /// A folder of either name beside a <c>project.godot</c> whose settings name it, holding only what
    /// the importer writes. Git is asked for a second opinion, for the reason
    /// <see cref="GodotDataFolder.Recognition"/> gives.
    /// </summary>
    private static BuildDirectoryKind KindOver(GodotProjectSurvey projects) => new()
    {
        DirectoryNames = [".import", "import"],
        RequiredSiblings = [GodotProjectSettings.ProjectFile],
        ConfirmsContents = (folder, ct) => projects.IsDataFolder(folder)
            && projects.Godot3ImportCache(folder, ct) is GodotCacheVerdict.Recognised,
        DeclinesTrackedDirectories = true,
    };

    public override string Id => "godot3-imported";

    public override string Name => "Godot 3 project import caches";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next time you open each project, the Godot 3 editor imports all of its assets again. On a "
        + "large project that takes minutes. Nothing is lost: all of it is made from the project's own "
        + "assets and their import settings, which stay.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "the Godot 3 editor",
        Publisher = "the Godot Engine project",
        Purpose = "Godot 3 converts every asset in a project into a form the engine loads quickly, and "
            + "keeps the result in a .import folder beside the project.godot file.",
        Recommendation = "Godot imports everything again by itself, but on a large project that takes "
            + "minutes. Deguffer removes the folder only where every file in it is one Godot's "
            + "importer wrote, and only for a project Godot 3 last saved.",
    };

    protected override string Subject => "a Godot 3 import cache";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for "
        + "Godot projects inside them, and nowhere else.";
}
