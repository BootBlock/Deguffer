using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Cemu, the Wii U emulator.
///
/// <para><b>One root on Windows.</b> Cemu's configuration, saves and caches all sit in one folder:
/// <c>portable</c> beside <c>Cemu.exe</c> where that directory exists, the program's own folder where
/// an install from before 2.0 left <c>settings.xml</c> there, and <c>%APPDATA%\Cemu</c> otherwise.
/// It writes no registry value saying which, so the portable layouts are reached only through a folder
/// the user declares.</para>
///
/// <para><b><c>transferable</c> is never a target, and it is the reason Cemu can be offered at
/// all.</b> It is the record of every shader and pipeline a game has used, it takes hours of play to
/// build, and it is often downloaded and shared between users rather than built locally, so a lost
/// one may not be obtainable again. <c>precompiled</c> is compiled from it for this GPU and this
/// version of Cemu, and <c>driver</c> holds the driver's own pipeline cache. Cemu rebuilds both from
/// <c>transferable</c> when a game next starts, which costs a compile pass before the game loads.
/// Cemu's own "remove shader caches" command deletes <c>transferable</c> as well, so it is not a route
/// §5.1 would prefer.</para>
/// </summary>
public sealed class CemuLayout : EmulatorLayout
{
    private const string CacheFolderName = "shaderCache";

    private static readonly DisposableChildSet ShaderCacheChildren = new(
    [
        new ChildClassification(
            "precompiled",
            SafetyTier.RegenerableWithCost,
            "Shaders Cemu compiled for this GPU and this version of Cemu. It compiles them again from the "
            + "transferable cache when each game next starts."),
        new ChildClassification(
            "driver",
            SafetyTier.RegenerableWithCost,
            "The graphics driver's pipeline cache for each game. Cemu and the driver build it again from "
            + "the transferable cache when each game next starts."),
        new ChildClassification(
            "transferable",
            SafetyTier.DoNotTouch,
            "The transferable shader cache: every shader each game has used, built up over hours of play "
            + "or downloaded. The other caches are compiled from it, and a downloaded one may not be "
            + "obtainable again."),
    ]);

    public override string Name => "Cemu";

    public override IReadOnlyList<string> ProcessNames { get; } = ["Cemu", "Cemu_release"];

    protected override IReadOnlyList<string> Markers { get; } = ["settings.xml"];

    public override IReadOnlyList<(string Name, string Reason)> ProtectedNames { get; } =
    [
        ("mlc01", "The emulated Wii U storage: your save data and installed games, updates and DLC."),
        ("graphicPacks", "Graphic packs you installed."),
        ("gameProfiles", "Settings you chose for each game."),
        ("controllerProfiles", "Your controller mappings."),
        ("keys.txt", "Your title keys."),
        ("otp.bin", "The Wii U console data you dumped."),
        ("seeprom.bin", "The Wii U console data you dumped."),
        ("settings.xml", "Cemu's settings."),
    ];

    public override IEnumerable<string> FixedRoots(IUserEnvironment environment)
    {
        yield return Path.Combine(environment.RoamingAppData, "Cemu");
    }

    public override IEnumerable<string> RootsDeclaredBy(string folder)
    {
        yield return Path.Combine(folder, "portable");
        yield return folder;
    }

    public override IReadOnlyList<EmulatorCacheFolder> CacheFoldersIn(string root) =>
    [
        new EmulatorCacheFolder(
            Path.Combine(root, CacheFolderName),
            TargetKind.Directory,
            ShaderCacheChildren.Classify,
            []),
    ];
}
