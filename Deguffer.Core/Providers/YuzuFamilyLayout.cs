using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One of the Nintendo Switch emulators descended from yuzu: Eden and Citron, which are maintained,
/// and yuzu itself, whose folder an old install leaves behind.
///
/// <para><b>One layout, three instances.</b> The forks share how they find their folder, prove it and
/// name each game's cache, and differ in the folder's name, where the cache sits in it, which renderers
/// write to it, and their programs. Each is an instance rather than a subclass, because those
/// differences are values, not behaviour.</para>
///
/// <para><b>Where it looks.</b> A <c>user</c> folder beside the program where one exists, and
/// <c>%APPDATA%\&lt;name&gt;</c> otherwise. Nothing records a portable install, so it is reached through
/// a folder the user declares. Every fork's portable folder is called <c>user</c> and holds the same
/// <c>config\qt-config.ini</c>, so a declared folder is read as a fork's only where that fork's
/// program is beside its <c>user</c> folder. Otherwise an Eden folder would also be read as Citron's,
/// and the old-layout <c>shader</c> folder a migration leaves in it, which Eden never reads, offered
/// as Citron's cache.</para>
///
/// <para><b>One game's driver cache at a time.</b> Each game's folder, named by its title id, holds
/// the guest shader cache (<c>vulkan.bin</c>, and <c>opengl.bin</c> where the fork has an OpenGL
/// renderer) and the graphics driver's pipeline cache (<c>vulkan_pipelines.bin</c>). The driver's
/// cache is compiled again from the guest cache when the game boots, so it is a target. The guest
/// cache is the record of every shader the game has used, built only by playing and shared between
/// players, the same case as Cemu's transferable cache, so it is never one. The emulator's own
/// "Remove All" deletes the whole game folder, guest cache included, so it is not a route §5.1
/// prefers.</para>
///
/// <para><b>Links.</b> Eden's migration can replace its folder with a junction to an old yuzu or
/// Citron folder, and its "Link to Ryujinx" puts junctions among its saves. A root that is a link is
/// declined rather than read, and nothing below a root is walked except a cache folder, so neither
/// is followed.</para>
///
/// <para><b>yuzu's layout is inferred.</b> Its source is no longer published. Its folder and
/// program names are the ones its forks renamed (Eden's build still names its targets <c>yuzu</c> and
/// <c>yuzu-cmd</c>), and its cache path is the <c>shader</c> folder Citron kept unchanged.</para>
/// </summary>
public sealed class YuzuFamilyLayout : EmulatorLayout
{
    private const string PortableFolder = "user";

    private const string GuestCacheReason =
        "The game's guest shader cache: every shader it has used, built up only by playing or shared by "
        + "other players. The driver's pipeline cache is compiled from it.";

    private const string PipelineCacheReason =
        "The graphics driver's pipeline cache for this game. The emulator compiles it again from the "
        + "guest shader cache when the game next boots, which takes longer before it starts.";

    private const string OwnPipelineCacheReason =
        "The graphics driver's cache of the emulator's own drawing pipelines, which it builds again when "
        + "it next starts.";

    private static readonly (string Name, string Reason)[] SharedProtectedNames =
    [
        ("keys", "Your Switch keys."),
        ("nand", "The emulated Switch storage: your saves, firmware and installed games, updates and DLC."),
        ("sdmc", "The emulated SD card."),
        ("load", "Mods you installed."),
        ("dump", "Game files you dumped."),
        ("amiibo", "Your amiibo data."),
        ("tas", "Your tool-assisted input scripts."),
        ("screenshots", "Your screenshots."),
        ("play_time", "How long you have played each game."),
        ("config", "The emulator's settings."),
    ];

    public static readonly YuzuFamilyLayout Eden = new(
        "Eden",
        "eden",
        ["eden", "eden-cli"],
        Path.Combine("cache", "shader"),
        ["vulkan.bin", "opengl.bin"],
        "vulkan_static_pipelines.bin",
        [
            .. SharedProtectedNames,
            ("lossless", "Lossless Scaling files you added."),
            ("post_shaders", "Post-processing shaders you installed."),
            ("post_presets", "Post-processing presets you saved."),
        ]);

    public static readonly YuzuFamilyLayout Citron = new(
        "Citron", "citron", ["citron", "citron-cmd"], "shader", ["vulkan.bin"], null, SharedProtectedNames);

    public static readonly YuzuFamilyLayout Yuzu = new(
        "yuzu", "yuzu", ["yuzu", "yuzu-cmd"], "shader", ["vulkan.bin", "opengl.bin"], null, SharedProtectedNames);

    private readonly string _folderName;
    private readonly string _shaderFolder;
    private readonly IReadOnlyList<string> _guestCacheFiles;
    private readonly string? _ownPipelineCache;

    private YuzuFamilyLayout(
        string name,
        string folderName,
        IReadOnlyList<string> processNames,
        string shaderFolder,
        IReadOnlyList<string> guestCacheFiles,
        string? ownPipelineCache,
        IReadOnlyList<(string Name, string Reason)> protectedNames)
    {
        Name = name;
        _folderName = folderName;
        ProcessNames = processNames;
        _shaderFolder = shaderFolder;
        _guestCacheFiles = guestCacheFiles;
        _ownPipelineCache = ownPipelineCache;
        ProtectedNames = protectedNames;
    }

    public override string Name { get; }

    public override IReadOnlyList<string> ProcessNames { get; }

    protected override IReadOnlyList<string> Markers { get; } = [Path.Combine("config", "qt-config.ini")];

    public override IReadOnlyList<(string Name, string Reason)> ProtectedNames { get; }

    public override IEnumerable<string> FixedRoots(IUserEnvironment environment)
    {
        yield return Path.Combine(environment.RoamingAppData, _folderName);
    }

    public override bool IsKnownByItsProgram => true;

    /// <summary>
    /// The <c>user</c> folder of this fork's program, where <paramref name="folder"/> is the program's
    /// folder or that <c>user</c> folder itself. Never a declared folder on its own word, because every
    /// fork's data folder looks alike.
    /// </summary>
    public override IEnumerable<string> RootsDeclaredBy(string folder) => FolderBesideProgram(folder, PortableFolder);

    /// <summary>
    /// The driver's cache of the emulator's own pipelines, which Eden keeps beside the game folders
    /// rather than in one. The other forks keep no such file.
    /// </summary>
    public override IReadOnlyList<EmulatorCacheFolder> CacheFoldersIn(string root) =>
        _ownPipelineCache is { } file
            ?
            [
                new EmulatorCacheFolder(
                    Path.Combine(root, _shaderFolder),
                    TargetKind.File,
                    name => name.Equals(file, StringComparison.OrdinalIgnoreCase)
                        ? new ChildClassification(name, SafetyTier.RegenerableWithCost, OwnPipelineCacheReason)
                        : new ChildClassification(name, SafetyTier.DoNotTouch, $"Not {Name}'s own pipeline cache, so it is left alone."),
                    []),
            ]
            : [];

    public override IReadOnlyList<EmulatorGameCaches> GameCachesIn(string root) =>
    [
        new EmulatorGameCaches(
            Path.Combine(root, _shaderFolder),
            game =>
            [
                new EmulatorCacheFolder(
                    game,
                    TargetKind.File,
                    ClassifyGameCacheFile,
                    [.. _guestCacheFiles.Select(file => (file, GuestCacheReason))]),
            ],
            []),
    ];

    private ChildClassification ClassifyGameCacheFile(string name) =>
        name.Equals("vulkan_pipelines.bin", StringComparison.OrdinalIgnoreCase)
            ? new ChildClassification(name, SafetyTier.RegenerableWithCost, PipelineCacheReason)
            : new ChildClassification(
                name,
                SafetyTier.DoNotTouch,
                _guestCacheFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? GuestCacheReason
                    : "Not the driver's pipeline cache, so it is left alone.");
}
