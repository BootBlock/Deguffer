using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// DuckStation, the PlayStation emulator.
///
/// <para><b>Where it looks, in DuckStation's own order.</b> The program's folder, where
/// <c>portable.txt</c> or <c>settings.ini</c> is there; <c>Documents\DuckStation</c> where an older
/// install left it; and <c>%LOCALAPPDATA%\DuckStation</c>. Both fixed folders are looked at, because an
/// older root in Documents can stay beside the current one. A portable install is reached through a
/// folder the user declares, and is read as DuckStation's only where its program is there, because
/// <c>settings.ini</c> is a name many programs write.</para>
///
/// <para><b>Files, never the folder.</b> The cache folder also holds <c>achievements.db</c>, the
/// record of unofficial achievement unlocks, which is the user's own and cannot be downloaded again,
/// beside the game list and downloaded images. Only the shader and pipeline files each renderer writes
/// are targets, by the names DuckStation gives them. <c>shaders</c> in the root holds the user's
/// post-processing shaders and is never reached.</para>
///
/// <para><b>The folder can move.</b> <c>[Folders] Cache</c> in <c>settings.ini</c> names it, relative to
/// the root or absolute, and DuckStation uses whatever it says.</para>
///
/// <para><b>Tier 2.</b> DuckStation compiles each shader and pipeline again as a game draws with it,
/// which stutters until it has. It discards the files itself when their version changes.</para>
/// </summary>
public sealed partial class DuckStationLayout : EmulatorLayout
{
    private const string SettingsFile = "settings.ini";

    private const string FolderName = "DuckStation";

    /// <summary>
    /// Large enough for any settings file DuckStation writes, and small enough that a file of some
    /// other kind by that name is refused rather than read.
    /// </summary>
    private const int MaximumSettingsBytes = 1024 * 1024;

    private const string ShaderFileReason =
        "Shaders or pipelines DuckStation compiled for this GPU. It compiles them again as each game "
        + "draws, which stutters until it has.";

    public override string Name => "DuckStation";

    /// <summary>
    /// Every name DuckStation's Windows build gives its program: <c>duckstation-qt</c>, the
    /// architecture and the configuration, with the SSE2 and MSVC variants where the build has them.
    /// </summary>
    public override IReadOnlyList<string> ProcessNames { get; } =
    [
        .. from architecture in new[] { "x64", "ARM64" }
           from configuration in new[] { "Debug", "DebugFast", "Release", "ReleaseLTCG", "Devel" }
           select $"duckstation-qt-{architecture}-{configuration}",
        .. from configuration in new[] { "Debug", "ReleaseLTCG" }
           select $"duckstation-qt-x64-{configuration}-SSE2",
        .. from configuration in new[] { "Debug", "DebugFast", "Release", "ReleaseLTCG" }
           select $"duckstation-qt-x64-{configuration}-MSVC",
    ];

    protected override IReadOnlyList<string> Markers { get; } = [SettingsFile];

    public override IReadOnlyList<(string Name, string Reason)> ProtectedNames { get; } =
    [
        ("bios", "The PlayStation BIOS you dumped. It cannot be downloaded."),
        ("memcards", "Your memory cards."),
        ("savestates", "Your save states."),
        ("shaders", "Post-processing shaders you installed. They are not the compiled cache."),
        ("cheats", "Cheats you added."),
        ("patches", "Patches you added."),
        ("textures", "Texture packs you installed and textures you dumped."),
        ("gamesettings", "Settings you chose for each game."),
        ("inputprofiles", "Your controller profiles."),
        ("covers", "Cover art you added or downloaded."),
        ("screenshots", "Your screenshots."),
        (SettingsFile, "DuckStation's settings."),
        ("playtime.dat", "How long you have played each game."),
    ];

    public override bool IsKnownByItsProgram => true;

    public override IEnumerable<string> FixedRoots(IUserEnvironment environment)
    {
        if (environment.Documents is { } documents)
        {
            yield return Path.Combine(documents, FolderName);
        }

        yield return Path.Combine(environment.LocalAppData, FolderName);
    }

    public override IEnumerable<string> RootsDeclaredBy(string folder)
    {
        if (HoldsProgram(folder))
        {
            yield return folder;
        }
    }

    public override IReadOnlyList<EmulatorCacheFolder> CacheFoldersIn(string root) =>
        IniSetting.Folder(root, Path.Combine(root, SettingsFile), "Folders", "Cache", "cache", MaximumSettingsBytes) is { } folder
            ?
            [
                new EmulatorCacheFolder(
                    folder,
                    TargetKind.File,
                    Classify,
                    [("achievements.db", "Your achievement unlocks recorded on this machine. They cannot be downloaded again.")]),
            ]
            : [];

    private static ChildClassification Classify(string name) =>
        ShaderFile().IsMatch(name)
            ? new ChildClassification(name, SafetyTier.RegenerableWithCost, ShaderFileReason)
            : new ChildClassification(name, SafetyTier.DoNotTouch, "Not a shader cache file, so it is left alone.");

    /// <summary>
    /// The files DuckStation's Direct3D 11, Direct3D 12, Vulkan and OpenGL renderers write to the cache
    /// folder: a shader archive's index and blob, and a pipeline cache, each with <c>_debug</c> where
    /// the renderer's debug device was on. A pipeline cache's temporary file from an interrupted write
    /// is not matched, and is left alone.
    /// </summary>
    [GeneratedRegex(
        @"^(?:d3d11|d3d12|vulkan|opengl)_(?:shaders(?:_debug)?\.(?:idx|bin)|pipelines(?:_debug)?\.bin)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ShaderFile();
}
