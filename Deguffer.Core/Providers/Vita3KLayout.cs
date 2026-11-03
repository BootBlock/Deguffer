using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Vita3K, the PlayStation Vita emulator.
///
/// <para><b>Found only through a folder the user declares.</b> Vita3K keeps its settings and its cache
/// beside <c>Vita3K.exe</c>, or in a <c>portable</c> folder there, wherever the archive was unpacked.
/// Only the emulated console's storage goes to <c>%APPDATA%\Vita3K\Vita3K</c>, and that holds no
/// shader cache. A declared folder is read as Vita3K's only where its program is there, because its
/// <c>config.yml</c> is the name an older RPCS3 gave its settings too.</para>
///
/// <para><b>One game's cache at a time.</b> Each folder in <c>cache\shaders</c>, named by a game's
/// title id, holds the shaders Vita3K translated from that game for this renderer, its index of them,
/// and on Vulkan the driver's pipeline cache, all derived from the game itself. Vita3K's own "Delete
/// Shader Caches" removes exactly that folder, but only from inside the running program, so there is
/// nothing §5.1 could call. The rest of <c>cache</c> is not shader cache and is never reached.</para>
///
/// <para><b>Tier 2.</b> Vita3K translates each shader again as the game draws with it, which stutters
/// until it has, and it discards the folder itself when its version or the GPU's features change.</para>
/// </summary>
public sealed partial class Vita3KLayout : EmulatorLayout
{
    private const string GameCacheReason =
        "The shaders Vita3K translated from one game for this GPU. It translates them again as the game "
        + "draws, which stutters until it has.";

    public override string Name => "Vita3K";

    public override IReadOnlyList<string> ProcessNames { get; } = ["Vita3K"];

    protected override IReadOnlyList<string> Markers { get; } = ["config.yml"];

    public override IReadOnlyList<(string Name, string Reason)> ProtectedNames { get; } =
    [
        ("config.yml", "Vita3K's settings."),
        ("config", "Settings you chose for each game."),
        ("gui-configs", "How Vita3K's window and game list are arranged."),
        ("textures", "Texture packs you installed and textures you dumped."),
        ("patch", "Patches you added."),
        (Path.Combine("cache", "decrypted_selfs"), "Game programs you asked Vita3K to decrypt."),
        ("fs", "The emulated PS Vita storage of a portable install: your saves, installed games, updates, DLC and firmware."),
    ];

    public override bool IsKnownByItsProgram => true;

    public override IEnumerable<string> FixedRoots(IUserEnvironment environment) => [];

    /// <summary>
    /// The <c>portable</c> folder beside Vita3K's program and the program's folder itself, where
    /// <paramref name="folder"/> is the program's folder, or that <c>portable</c> folder.
    /// </summary>
    public override IEnumerable<string> RootsDeclaredBy(string folder)
    {
        foreach (var portable in FolderBesideProgram(folder, "portable"))
        {
            yield return portable;
        }

        if (HoldsProgram(folder))
        {
            yield return folder;
        }
    }

    public override IReadOnlyList<EmulatorCacheFolder> CacheFoldersIn(string root) =>
    [
        new EmulatorCacheFolder(Path.Combine(root, "cache", "shaders"), TargetKind.Directory, Classify, []),
    ];

    private static ChildClassification Classify(string name) =>
        TitleId().IsMatch(name)
            ? new ChildClassification(name, SafetyTier.RegenerableWithCost, GameCacheReason)
            : new ChildClassification(name, SafetyTier.DoNotTouch, "Not one game's shader cache, so it is left alone.");

    /// <summary>
    /// A PS Vita title id: four letters and five digits for a published game, such as
    /// <c>PCSE00001</c>, and any nine letters or digits for homebrew, such as <c>VITASHELL</c>.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9]{9}$")]
    private static partial Regex TitleId();
}
