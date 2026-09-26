using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Dolphin, the GameCube and Wii emulator.
///
/// <para><b>Where it looks, in Dolphin's own order.</b> Beside <c>Dolphin.exe</c> in a <c>User</c>
/// folder where <c>portable.txt</c> is there; the path in <c>HKCU\Software\Dolphin Emulator</c>'s
/// <c>UserConfigPath</c>; <c>Documents\Dolphin Emulator</c> where it exists; and
/// <c>%APPDATA%\Dolphin Emulator</c>, after which Dolphin writes that path to the registry itself. Every
/// candidate is looked at rather than only the first, because an older install can leave a proven root
/// in Documents beside the current one, and a shader cache in either is Dolphin's.</para>
///
/// <para><b>The collision this layout is written against.</b> <c>Shaders</c> in the root holds the
/// post-processing shaders the user installed, and <c>Cache\Shaders</c> is the compiled cache. Only
/// the second is a target, by its path from the root. Nothing else in <c>Cache</c> is: the game list,
/// the covers and the Redump and achievement downloads are cheap to lose but are not shader caches,
/// and each <c>.uidcache</c> is the list Dolphin precompiles from, so removing it brings back the
/// stutter that removing the cache defers. The legacy <c>ShaderCache</c> is not a target either,
/// because Dolphin deletes it itself at every start.</para>
///
/// <para><b>Tier 2 on Dolphin's own word.</b> Its graphics settings say that stuttering will occur
/// during shader compilation, and that precompiling costs a longer delay before the game starts.</para>
/// </summary>
public sealed class DolphinLayout : EmulatorLayout
{
    /// <summary>The key Dolphin writes its user folder to, relative to <c>HKEY_CURRENT_USER</c>.</summary>
    public const string RegistryKey = @"Software\Dolphin Emulator";

    public const string RegistryValue = "UserConfigPath";

    private const string FolderName = "Dolphin Emulator";

    private static readonly DisposableChildSet CacheChildren = new(
    [
        new ChildClassification(
            "Shaders",
            SafetyTier.RegenerableWithCost,
            "Shaders Dolphin compiled for this GPU. It compiles them again as each game is played, which "
            + "stutters, or before the game starts if it is set to precompile."),
    ]);

    public override string Name => "Dolphin";

    public override IReadOnlyList<string> ProcessNames { get; } = ["Dolphin", "DolphinNoGUI"];

    protected override IReadOnlyList<string> Markers { get; } = [Path.Combine("Config", "Dolphin.ini")];

    public override IReadOnlyList<(string Name, string Reason)> ProtectedNames { get; } =
    [
        ("Shaders", "Post-processing shaders you installed. They share a name with the compiled cache, "
            + "and are not it."),
        ("GC", "Your GameCube memory cards."),
        ("Wii", "The emulated Wii's storage: your saves, installed channels and settings."),
        ("GBA", "Game Boy Advance saves and the BIOS you dumped."),
        ("StateSaves", "Your save states."),
        ("Load", "Content you supplied, including the virtual SD card."),
        ("Config", "Dolphin's settings."),
        ("GameSettings", "Settings you chose for each game."),
        ("ResourcePacks", "Resource packs you installed."),
        ("ScreenShots", "Your screenshots."),
    ];

    public override IEnumerable<string> FixedRoots(IUserEnvironment environment)
    {
        if (LongPath.Configured(environment.ReadCurrentUserRegistryValue(RegistryKey, RegistryValue)) is { } written)
        {
            yield return written;
        }

        if (environment.Documents is { } documents)
        {
            yield return Path.Combine(documents, FolderName);
        }

        yield return Path.Combine(environment.RoamingAppData, FolderName);
    }

    public override IEnumerable<string> RootsDeclaredBy(string folder)
    {
        yield return Path.Combine(folder, "User");
        yield return folder;
    }

    public override IReadOnlyList<EmulatorCacheFolder> CacheFoldersIn(string root) =>
    [
        new EmulatorCacheFolder(Path.Combine(root, "Cache"), TargetKind.Directory, CacheChildren.Classify, []),
    ];
}
