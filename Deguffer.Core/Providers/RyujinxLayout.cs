using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Ryujinx, the Nintendo Switch emulator, as the maintained Ryubing builds are and as an install of
/// the original left it.
///
/// <para><b>Where it looks.</b> <c>%APPDATA%\Ryujinx</c>, unless a <c>portable</c> folder sits beside
/// the program, in which case that folder. A <c>--root-data-dir</c> on the command line overrides
/// both and is recorded nowhere, so it and a portable install are reached through a folder the user
/// declares.</para>
///
/// <para><b>One game's caches at a time, inside its own folder.</b> Each game's folder in
/// <c>games</c>, named by its title id, holds its settings beside <c>cache\shader</c> and
/// <c>cache\cpu</c>. In <c>cache\shader</c> only the host files are targets: the shaders compiled for
/// this graphics API and vendor, which Ryujinx compiles again from the guest files when the game
/// boots. The guest and shared files are the record of every shader the game has used, built only by
/// playing and often shared between players, the same case as Cemu's transferable cache, so they are
/// never targets. In <c>cache\cpu</c>, the PPTC cache's two folders are targets, because Ryujinx
/// translates the game's code again over its next few boots.</para>
///
/// <para><b>§5.1 has nothing to prefer.</b> Ryujinx's own purge of a game's shader cache removes
/// the guest files too.</para>
/// </summary>
public sealed partial class RyujinxLayout : EmulatorLayout
{
    private const string HostShaderReason =
        "Shaders Ryujinx compiled for this graphics card. It compiles them again from the game's guest "
        + "shader cache the next time the game boots, which takes longer before it starts.";

    private const string GuestShaderReason =
        "The game's guest shader cache: every shader it has used, built up only by playing or shared by "
        + "other players. Ryujinx compiles the host cache from it.";

    private const string CpuCacheReason =
        "The game's translated code (the PPTC cache). Ryujinx translates the game again over its next "
        + "few boots, which makes them slower to start.";

    private static readonly DisposableChildSet CpuCacheChildren = new(
    [
        new ChildClassification("0", SafetyTier.RegenerableWithCost, CpuCacheReason),
        new ChildClassification("1", SafetyTier.RegenerableWithCost, CpuCacheReason),
    ]);

    public override string Name => "Ryujinx";

    public override IReadOnlyList<string> ProcessNames { get; } = ["Ryujinx"];

    /// <summary>
    /// The settings file Ryujinx writes to its folder, under the name a pull-request build gives it
    /// too. A <c>Config.json</c> beside the program takes precedence and leaves none in the folder, so
    /// such an install is not proven.
    /// </summary>
    protected override IReadOnlyList<string> Markers { get; } = ["Config.json", "PRConfig.json"];

    public override IReadOnlyList<(string Name, string Reason)> ProtectedNames { get; } =
    [
        ("system", "Your Switch keys and firmware."),
        ("bis", "The emulated Switch storage: your saves and installed games, updates and DLC."),
        ("sdcard", "The emulated SD card."),
        ("profiles", "Your user profiles."),
        ("mods", "Mods you installed."),
        ("Config.json", "Ryujinx's settings."),
        ("PRConfig.json", "The settings of a Ryujinx test build."),
    ];

    public override IEnumerable<string> FixedRoots(IUserEnvironment environment)
    {
        yield return Path.Combine(environment.RoamingAppData, "Ryujinx");
    }

    public override IEnumerable<string> RootsDeclaredBy(string folder)
    {
        yield return Path.Combine(folder, "portable");
        yield return folder;
    }

    public override IReadOnlyList<EmulatorCacheFolder> CacheFoldersIn(string root) => [];

    public override IReadOnlyList<EmulatorGameCaches> GameCachesIn(string root) =>
    [
        new EmulatorGameCaches(
            Path.Combine(root, "games"),
            SwitchTitleId.IsMatch,
            game =>
            [
                new EmulatorCacheFolder(
                    Path.Combine(game, "cache", "shader"),
                    TargetKind.File,
                    ClassifyShaderFile,
                    [
                        ("guest.toc", GuestShaderReason),
                        ("guest.data", GuestShaderReason),
                        ("shared.toc", GuestShaderReason),
                        ("shared.data", GuestShaderReason),
                    ]),
                new EmulatorCacheFolder(
                    Path.Combine(game, "cache", "cpu"),
                    TargetKind.Directory,
                    CpuCacheChildren.Classify,
                    []),
            ],
            [
                ("Config.json", "Settings you chose for this game."),
                ("gui", "What Ryujinx shows for this game in its list."),
            ]),
    ];

    private static ChildClassification ClassifyShaderFile(string name) =>
        HostShaderFile().IsMatch(name)
            ? new ChildClassification(name, SafetyTier.RegenerableWithCost, HostShaderReason)
            : new ChildClassification(
                name,
                SafetyTier.DoNotTouch,
                GuestShaderFile().IsMatch(name) ? GuestShaderReason : "Not a host shader cache file, so it is left alone.");

    [GeneratedRegex(@"^(?:guest|shared)\.(?:toc|data)$", RegexOptions.IgnoreCase)]
    private static partial Regex GuestShaderFile();

    /// <summary>
    /// The host cache's table of contents and data, named by the graphics API and the vendor Ryujinx
    /// compiled for, such as <c>vulkan_nvidia.toc</c>. The vendor is the driver's own name with only
    /// the characters a file name cannot hold removed, so it may hold a dot.
    /// </summary>
    [GeneratedRegex(@"^(?:opengl|vulkan)_.+\.(?:toc|data)$", RegexOptions.IgnoreCase)]
    private static partial Regex HostShaderFile();
}
