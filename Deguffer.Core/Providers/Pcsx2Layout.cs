using System.Text;
using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// PCSX2, the PlayStation 2 emulator.
///
/// <para><b>Where it looks.</b> <c>Documents\PCSX2</c>, unless <c>portable.ini</c> or
/// <c>portable.txt</c> sits beside the program, in which case the program's folder, or the folder
/// <c>portable.txt</c> names relative to it. Nothing records a portable install, so those are reached
/// through a folder the user declares.</para>
///
/// <para><b>Files, never the folder.</b> PCSX2 keeps more than shaders in its cache folder: the game
/// list, achievement badges, and the on-screen fonts the user downloaded, which a setting can point
/// at. So only the shader and pipeline files each renderer writes there are targets, by the names
/// PCSX2 gives them, and everything else in the folder survives.</para>
///
/// <para><b>The folder can move.</b> <c>[Folders] Cache</c> in <c>inis\PCSX2.ini</c> names it,
/// relative to the root or absolute, and PCSX2 uses whatever it says. It is read rather than assumed,
/// and a value that cannot be read leaves the default in place, which is the folder PCSX2 falls back
/// to as well.</para>
///
/// <para><b>Tier 2 by inference.</b> PCSX2 compiles each shader again when a game first draws with it
/// after the cache has gone, and discards the files itself when its cache version changes. No vendor
/// statement of the cost was found, so it is held to the same tier as the other emulators.</para>
/// </summary>
public sealed partial class Pcsx2Layout : EmulatorLayout
{
    private const string SettingsFile = "PCSX2.ini";

    private const string DefaultCacheFolder = "cache";

    /// <summary>
    /// Large enough for any settings file PCSX2 writes, which holds a few hundred short lines, and
    /// small enough that a file of some other kind by that name is refused rather than read.
    /// </summary>
    private const int MaximumSettingsBytes = 1024 * 1024;

    private const string ShaderFileReason =
        "Shaders or pipelines PCSX2 compiled for this GPU. It compiles them again as each game draws, "
        + "which stutters until it has.";

    public override string Name => "PCSX2";

    /// <summary>
    /// Every name PCSX2's build gives its program: <c>pcsx2-qt</c>, then the architecture, the
    /// instruction set, the compiler and the build type, each where the build has one.
    /// </summary>
    public override IReadOnlyList<string> ProcessNames { get; } =
    [
        .. from architecture in new[] { "", "x64" }
           from instructions in new[] { "", "-avx2" }
           from compiler in new[] { "", "-clang" }
           from build in new[] { "", "-dbg", "-dev" }
           select "pcsx2-qt" + architecture + instructions + compiler + build,
    ];

    protected override IReadOnlyList<string> Markers { get; } = [Path.Combine("inis", SettingsFile)];

    public override IReadOnlyList<(string Name, string Reason)> ProtectedNames { get; } =
    [
        ("bios", "The PS2 BIOS you dumped. It cannot be downloaded."),
        ("memcards", "Your memory cards."),
        ("sstates", "Your save states."),
        ("inis", "PCSX2's settings."),
        ("cheats", "Cheats you added."),
        ("patches", "Patches you added."),
        ("textures", "Texture packs you installed and textures you dumped."),
        ("covers", "Cover art you added or downloaded."),
    ];

    public override IEnumerable<string> FixedRoots(IUserEnvironment environment)
    {
        if (environment.Documents is { } documents)
        {
            yield return Path.Combine(documents, "PCSX2");
        }
    }

    public override IEnumerable<string> RootsDeclaredBy(string folder)
    {
        if (PortableFolderNamedIn(folder) is { } named)
        {
            yield return named;
        }

        yield return folder;
    }

    public override IReadOnlyList<EmulatorCacheFolder> CacheFoldersIn(string root)
    {
        var configured = SettingIn(root, "Folders", "Cache") is { Length: > 0 } value ? value : DefaultCacheFolder;
        var folder = Path.IsPathFullyQualified(configured)
            ? LongPath.Configured(configured)
            : LongPath.Configured(Path.Combine(root, configured));

        return folder is null
            ? []
            :
            [
                new EmulatorCacheFolder(
                    folder,
                    TargetKind.File,
                    Classify,
                    [("fonts", "On-screen display fonts you downloaded. PCSX2 may be set to use one.")]),
            ];
    }

    private static ChildClassification Classify(string name) =>
        ShaderFile().IsMatch(name)
            ? new ChildClassification(name, SafetyTier.RegenerableWithCost, ShaderFileReason)
            : new ChildClassification(name, SafetyTier.DoNotTouch, "Not a shader cache file, so it is left alone.");

    /// <summary>
    /// The folder <c>portable.txt</c> names relative to <paramref name="folder"/>, or null where there
    /// is no such file, it names nothing, or it cannot be read. PCSX2 reads the whole file, trimmed,
    /// as one path.
    /// </summary>
    private static string? PortableFolderNamedIn(string folder)
    {
        if (BoundedFile.Read(Path.Combine(folder, "portable.txt"), MaximumSettingsBytes) is not { } content
            || Encoding.UTF8.GetString(content.Span).Trim() is not { Length: > 0 } named)
        {
            return null;
        }

        return LongPath.Configured(Path.Combine(folder, named));
    }

    /// <summary>
    /// A value from <paramref name="root"/>'s settings file, or null where the file, the section or the
    /// key is missing or the file cannot be read.
    /// </summary>
    private static string? SettingIn(string root, string section, string key)
    {
        if (BoundedFile.Read(Path.Combine(root, "inis", SettingsFile), MaximumSettingsBytes) is not { } content)
        {
            return null;
        }

        var inSection = false;

        foreach (var raw in Encoding.UTF8.GetString(content.Span).Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inSection
                && line.IndexOf('=') is > 0 and var equals
                && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return line[(equals + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// The files PCSX2's Direct3D 11, Direct3D 12, Vulkan and OpenGL renderers write to the cache
    /// folder: an index and a blob each, with <c>_debug</c> where the renderer's debug device was on.
    /// </summary>
    [GeneratedRegex(
        @"^(?:(?:d3d_shaders_sm[0-9]+|d3d12_(?:shaders|pipelines)_sm[0-9]+|vulkan_shaders)(?:_debug)?\.(?:idx|bin)|vulkan_pipelines(?:_debug)?\.bin|gl_programs\.(?:idx|bin))$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ShaderFile();
}
