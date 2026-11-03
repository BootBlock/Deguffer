using System.Text;
using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// PPSSPP, the PlayStation Portable emulator.
///
/// <para><b>Where it looks, as PPSSPP chooses its memory stick.</b> Where <c>installed.txt</c> sits
/// beside the program, the folder its first line names, and <c>Documents\PPSSPP</c> where that line
/// is empty or names no usable folder, which is what the installer's own <c>installed.txt</c> comes
/// to. Otherwise <c>memstick</c> beside the program. A <c>--memstick</c> on the command line overrides
/// all of them and is recorded nowhere, so it and an unpacked install are reached through a folder the
/// user declares.</para>
///
/// <para><b>Files, never the folder.</b> <c>PSP\SYSTEM\CACHE</c> also holds the copies of game images
/// read from a slow drive, downloads and achievement icons. Only the shader caches are targets: one
/// file for each game and renderer, named by the game's id, which is not always of one form, so it is
/// recognised by the extension PPSSPP gives it, and the built-in shaders' SPIR-V cache beside them.
/// <c>PSP\SHADERS</c> holds the user's own post-processing shaders and is never reached.</para>
///
/// <para><b>Tier 2.</b> PPSSPP compiles each shader again as a game draws with it, which stutters
/// until it has. It discards a cache itself when its version or the driver changes.</para>
/// </summary>
public sealed partial class PpssppLayout : EmulatorLayout
{
    private const string FolderName = "PPSSPP";

    /// <summary>
    /// Large enough for the path <c>installed.txt</c> holds, and small enough that a file of some other
    /// kind by that name is refused rather than read.
    /// </summary>
    private const int MaximumInstalledBytes = 64 * 1024;

    private const string ShaderCacheReason =
        "Shaders PPSSPP compiled for this GPU. It compiles them again as each game draws, which stutters "
        + "until it has.";

    public override string Name => "PPSSPP";

    /// <summary>The release and debug programs PPSSPP's Windows project builds, for each architecture.</summary>
    public override IReadOnlyList<string> ProcessNames { get; } =
    [
        "PPSSPPWindows64",
        "PPSSPPWindows",
        "PPSSPPWindowsARM64",
        "PPSSPPDebug64",
        "PPSSPPDebug",
        "PPSSPPDebugARM64",
    ];

    protected override IReadOnlyList<string> Markers { get; } = [Path.Combine("PSP", "SYSTEM", "ppsspp.ini")];

    public override IReadOnlyList<(string Name, string Reason)> ProtectedNames { get; } =
    [
        (Path.Combine("PSP", "SAVEDATA"), "Your saves."),
        (Path.Combine("PSP", "PPSSPP_STATE"), "Your save states."),
        (Path.Combine("PSP", "GAME"), "Games, updates and homebrew you installed."),
        (Path.Combine("PSP", "SHADERS"), "Post-processing shaders you installed. They are not the compiled cache."),
        (Path.Combine("PSP", "TEXTURES"), "Texture packs you installed."),
        (Path.Combine("PSP", "Cheats"), "Cheats you added."),
        (Path.Combine("PSP", "PLUGINS"), "Plugins you installed."),
        (Path.Combine("PSP", "SCREENSHOT"), "Your screenshots."),
        (Path.Combine("PSP", "NAND"), "The PSP firmware files you supplied."),
        (Path.Combine("PSP", "SYSTEM", "ppsspp.ini"), "PPSSPP's settings."),
    ];

    public override IEnumerable<string> FixedRoots(IUserEnvironment environment)
    {
        if (environment.Documents is { } documents)
        {
            yield return Path.Combine(documents, FolderName);
        }
    }

    public override IEnumerable<string> RootsDeclaredBy(string folder)
    {
        if (FolderNamedIn(folder) is { } named)
        {
            yield return named;
        }

        yield return Path.Combine(folder, "memstick");
        yield return folder;
    }

    public override IReadOnlyList<EmulatorCacheFolder> CacheFoldersIn(string root) =>
    [
        new EmulatorCacheFolder(Path.Combine(root, "PSP", "SYSTEM", "CACHE"), TargetKind.File, Classify, []),
    ];

    private static ChildClassification Classify(string name) =>
        ShaderCacheFile().IsMatch(name)
            ? new ChildClassification(name, SafetyTier.RegenerableWithCost, ShaderCacheReason)
            : new ChildClassification(name, SafetyTier.DoNotTouch, "Not a shader cache file, so it is left alone.");

    /// <summary>
    /// The folder the first line of <c>installed.txt</c> in <paramref name="folder"/> names, or null
    /// where there is no such file or it names no absolute path. An empty line means Documents, which
    /// is a fixed root already, and anything else is a folder PPSSPP cannot make and leaves for
    /// Documents too.
    /// </summary>
    private static string? FolderNamedIn(string folder)
    {
        if (BoundedFile.Read(Path.Combine(folder, "installed.txt"), MaximumInstalledBytes) is not { } content)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(content.Span);
        var line = text.IndexOf('\n') is >= 0 and var end ? text[..end] : text;

        return Path.IsPathFullyQualified(line) ? LongPath.Configured(line) : null;
    }

    /// <summary>
    /// A game's Vulkan or OpenGL shader cache, named by its id and the renderer's extension, and the
    /// SPIR-V cache of PPSSPP's own shaders that every game shares.
    /// </summary>
    [GeneratedRegex(@"^(?:.+\.(?:vk|gl)shadercache|vulkan_spirv\.cache)$", RegexOptions.IgnoreCase)]
    private static partial Regex ShaderCacheFile();
}
