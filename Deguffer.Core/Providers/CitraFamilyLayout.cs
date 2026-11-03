using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One of the Nintendo 3DS emulators descended from Citra: Azahar, which is maintained, and Lime3DS and
/// Citra, whose folders stay behind after Azahar's migration, which copies them rather than moving
/// them.
///
/// <para><b>One layout, three instances,</b> as <see cref="YuzuFamilyLayout"/> is: they share how they
/// find their folder, prove it and lay out their shader caches, and differ in the folder's name and
/// their programs.</para>
///
/// <para><b>Where it looks.</b> A <c>user</c> folder beside the program where one exists, and
/// <c>%APPDATA%\&lt;name&gt;</c> otherwise. Nothing records a portable install, so it is reached through
/// a folder the user declares. Every fork's portable folder is called <c>user</c> and holds
/// <c>config\qt-config.ini</c>, as the yuzu family's does, so a declared folder is read as a fork's
/// only where that fork's program is beside its <c>user</c> folder.</para>
///
/// <para><b>Recognised paths inside <c>shaders</c>, never <c>shaders</c> itself.</b> The same folder
/// holds the user's own post-processing shaders, loose <c>.glsl</c> files and the <c>anaglyph</c>
/// folder. Only these are targets:</para>
/// <list type="bullet">
/// <item><c>shaders\opengl\precompiled</c>: OpenGL programs compiled for this driver, which the
/// emulator compiles again from the transferable cache when each game boots.</item>
/// <item><c>shaders\vulkan\pipeline\&lt;title id&gt;-&lt;GPU&gt;.bin</c>: Azahar's per-game Vulkan
/// pipeline cache for this GPU.</item>
/// <item><c>shaders\vulkan\&lt;GPU&gt;.bin</c>: the one Vulkan pipeline cache of Citra, Lime3DS and early
/// Azahar, which a migration copies into Azahar's folder where nothing reads it.</item>
/// </list>
///
/// <para><b>The transferable caches are never targets.</b> <c>shaders\opengl\transferable</c> and
/// <c>shaders\vulkan\transferable</c> record every shader each game has used, built only by playing,
/// the same case as Cemu's transferable cache. Azahar's Vulkan <c>.vkch</c> files mix that record with
/// the SPIR-V compiled from it, so they are kept whole. Azahar's own "Delete Shader Cache" commands
/// remove the transferable caches too, so they are not a route §5.1 prefers.</para>
/// </summary>
public sealed partial class CitraFamilyLayout : EmulatorLayout
{
    private const string PortableFolder = "user";

    private const string TransferableReason =
        "The transferable shader cache: every shader each game has used, built up only by playing. The "
        + "compiled caches are built from it.";

    private const string PipelineReason =
        "The graphics driver's Vulkan pipeline cache for this GPU. The emulator builds it again as each "
        + "game is played, which stutters until it has.";

    private static readonly DisposableChildSet OpenGlChildren = new(
    [
        new ChildClassification(
            "precompiled",
            SafetyTier.RegenerableWithCost,
            "OpenGL programs compiled for this graphics driver. The emulator compiles them again from the "
            + "transferable cache when each game next boots, which takes longer before it starts."),
        new ChildClassification("transferable", SafetyTier.DoNotTouch, TransferableReason),
    ]);

    public static readonly CitraFamilyLayout Azahar = new("Azahar", ["azahar"]);

    public static readonly CitraFamilyLayout Lime3DS = new("Lime3DS", ["lime3ds", "lime3ds-gui", "lime3ds-cli"]);

    public static readonly CitraFamilyLayout Citra = new("Citra", ["citra-qt", "citra"]);

    private CitraFamilyLayout(string name, IReadOnlyList<string> processNames)
    {
        Name = name;
        ProcessNames = processNames;
    }

    public override string Name { get; }

    public override IReadOnlyList<string> ProcessNames { get; }

    /// <summary>The settings file of the Qt front end, and of the SDL one a user may run alone.</summary>
    protected override IReadOnlyList<string> Markers { get; } =
        [Path.Combine("config", "qt-config.ini"), Path.Combine("config", "sdl2-config.ini")];

    public override IReadOnlyList<(string Name, string Reason)> ProtectedNames { get; } =
    [
        ("sdmc", "The emulated SD card: your installed games, saves and extra data."),
        ("nand", "The emulated 3DS system storage: system saves and titles."),
        ("sysdata", "Your keys, the console files you dumped, and how long you have played each game."),
        ("states", "Your save states."),
        ("cheats", "Cheats you added."),
        ("load", "Mods and custom textures you installed."),
        ("dump", "Game files and textures you dumped."),
        ("config", "The emulator's settings."),
        ("log", "The emulator's log, and in Lime3DS how long you have played each game."),
        (Path.Combine("shaders", "anaglyph"), "Anaglyph shaders you installed."),
    ];

    public override bool IsKnownByItsProgram => true;

    public override IEnumerable<string> FixedRoots(IUserEnvironment environment)
    {
        yield return Path.Combine(environment.RoamingAppData, Name);
    }

    public override IEnumerable<string> RootsDeclaredBy(string folder) => FolderBesideProgram(folder, PortableFolder);

    public override IReadOnlyList<EmulatorCacheFolder> CacheFoldersIn(string root)
    {
        var vulkan = Path.Combine(root, "shaders", "vulkan");

        return
        [
            new EmulatorCacheFolder(Path.Combine(root, "shaders", "opengl"), TargetKind.Directory, OpenGlChildren.Classify, []),
            new EmulatorCacheFolder(vulkan, TargetKind.File, ClassifyVulkanFile, [("transferable", TransferableReason)]),
            new EmulatorCacheFolder(Path.Combine(vulkan, "pipeline"), TargetKind.File, ClassifyGamePipelineFile, []),
        ];
    }

    private static ChildClassification ClassifyVulkanFile(string name) =>
        GpuPipelineFile().IsMatch(name)
            ? new ChildClassification(name, SafetyTier.RegenerableWithCost, PipelineReason)
            : new ChildClassification(
                name,
                SafetyTier.DoNotTouch,
                name.Equals("transferable", StringComparison.OrdinalIgnoreCase)
                    ? TransferableReason
                    : "Not a Vulkan pipeline cache, so it is left alone.");

    private static ChildClassification ClassifyGamePipelineFile(string name) =>
        GamePipelineFile().IsMatch(name)
            ? new ChildClassification(name, SafetyTier.RegenerableWithCost, PipelineReason)
            : new ChildClassification(name, SafetyTier.DoNotTouch, "Not a game's Vulkan pipeline cache, so it is left alone.");

    /// <summary>
    /// The one pipeline cache of Citra, Lime3DS and early Azahar, named by the GPU's vendor and device
    /// ids in lower-case hexadecimal, run together.
    /// </summary>
    [GeneratedRegex("^[0-9a-f]+\\.bin$", RegexOptions.IgnoreCase)]
    private static partial Regex GpuPipelineFile();

    /// <summary>
    /// Azahar's pipeline cache for one game on one GPU: the 16-digit title id, then the vendor and
    /// device ids. The marker it leaves while it loads one is not a cache, and is left alone.
    /// </summary>
    [GeneratedRegex("^[0-9A-F]{16}-[0-9A-F]+\\.bin$", RegexOptions.IgnoreCase)]
    private static partial Regex GamePipelineFile();
}
