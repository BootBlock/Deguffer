using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// RPCS3, the PlayStation 3 emulator.
///
/// <para><b>No fixed location on Windows.</b> RPCS3 keeps everything beside <c>rpcs3.exe</c>, or in a
/// <c>portable</c> folder there, wherever the archive was unpacked. The one exception is the
/// <c>RPCS3_CONFIG_DIR</c> variable, which it reads as it does: separators made forward slashes, then
/// cut back to the last one, so a value without a trailing separator names its parent. Every other
/// install is reached through a folder the user declares.</para>
///
/// <para><b>One game's cache at a time, never <c>cache</c> itself.</b> Each game's folder in
/// <c>cache</c>, named by its serial, holds the PPU and SPU modules RPCS3 compiled and the shaders it
/// compiled for it, and RPCS3's own "Remove All Caches" removes exactly that folder. The rest of
/// <c>cache</c> is not one game's cache: <c>playlists</c> holds custom soundtracks the user built, and
/// the compiled firmware beside it is not offered for want of evidence of what rebuilding it costs.</para>
///
/// <para><b>Tier 2.</b> The next boot of each game recompiles its PPU and SPU modules before it
/// starts, a pass slow enough to be taken for a crash, and its shaders stutter until they are compiled
/// again.</para>
/// </summary>
public sealed partial class Rpcs3Layout : EmulatorLayout
{
    public const string ConfigDirectoryVariable = "RPCS3_CONFIG_DIR";

    private const string GameCacheReason =
        "One game's compiled PPU and SPU modules and shaders. RPCS3 compiles them again the next time "
        + "the game boots, which takes minutes before it starts.";

    private static readonly ChildClassification Playlists = new(
        "playlists",
        SafetyTier.DoNotTouch,
        "Custom soundtrack playlists you built in games that support them.");

    public override string Name => "RPCS3";

    public override IReadOnlyList<string> ProcessNames { get; } = ["rpcs3", "rpcs3d"];

    protected override IReadOnlyList<string> Markers { get; } = [Path.Combine("config", "config.yml"), "config.yml"];

    public override IReadOnlyList<(string Name, string Reason)> ProtectedNames { get; } =
    [
        ("dev_hdd0", "The emulated PS3 hard drive: your saves, installed games, updates and DLC."),
        ("dev_flash", "The PS3 firmware you installed."),
        ("savestates", "Your save states."),
        ("captures", "Your recordings and screenshots."),
        ("config", "RPCS3's settings."),
    ];

    public override IEnumerable<string> FixedRoots(IUserEnvironment environment)
    {
        if (environment.GetEnvironmentVariable(ConfigDirectoryVariable) is { } value
            && value.Replace('/', '\\') is var separated
            && separated.LastIndexOf('\\') is > 0 and var last
            && LongPath.Configured(separated[..(last + 1)]) is { } root)
        {
            yield return root;
        }
    }

    public override IEnumerable<string> RootsDeclaredBy(string folder)
    {
        yield return Path.Combine(folder, "portable");
        yield return folder;
    }

    public override IReadOnlyList<EmulatorCacheFolder> CacheFoldersIn(string root) =>
    [
        new EmulatorCacheFolder(
            Path.Combine(root, "cache"),
            TargetKind.Directory,
            Classify,
            [(Playlists.Name, Playlists.Reason)]),
    ];

    private static ChildClassification Classify(string name) =>
        Serial().IsMatch(name)
            ? new ChildClassification(name, SafetyTier.RegenerableWithCost, GameCacheReason)
            : name.Equals(Playlists.Name, StringComparison.OrdinalIgnoreCase)
                ? Playlists
                : new ChildClassification(name, SafetyTier.DoNotTouch, "Not one game's cache, so it is left alone.");

    /// <summary>A PlayStation 3 title serial, such as <c>BLUS30443</c> or <c>NPEB00874</c>.</summary>
    [GeneratedRegex("^[A-Z]{4}[0-9]{5}$")]
    private static partial Regex Serial();
}
