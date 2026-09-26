using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The shader sets and game databases RetroArch's online updater downloads and never removes.
///
/// <para><b>Tier 2.</b> Each is downloaded again through its own entry in Online Updater, which is a
/// re-download rather than a recompilation, and so a different fact from the emulator shader caches.
/// Nothing is lost, but until it is downloaded again a preset that uses a removed shader does not
/// load, and a scan for new games finds none.</para>
///
/// <para><b>Only what the updater writes, by the name it writes it under.</b> The updater extracts each
/// shader set into a folder of its own name in the shader folder, so only <c>shaders_slang</c>,
/// <c>shaders_glsl</c> and <c>shaders_cg</c> are taken there: the presets the user saves sit in the
/// shader folder itself. It extracts the databases straight into the database folder, so only the
/// <c>.rdb</c> files are taken there, and a folder the user pointed the setting at keeps everything
/// else.</para>
///
/// <para><b>Updater entries left alone on purpose.</b> Cheats, controller profiles and overlays are
/// extracted beside the user's own files with no record of which is which, and the controller
/// profiles only in part. Cores are updated only where installed, so after a removal each would be
/// installed again by hand. <c>downloads</c> is where the content downloader puts content, and
/// <c>system</c> holds BIOS files the libretro servers cannot carry.</para>
/// </summary>
public sealed class RetroArchDownloadProvider : RetroArchProviderBase
{
    private const string ShaderGroup = "Shaders";

    private const string DatabaseGroup = "Game databases";

    private const string DatabaseExtension = ".rdb";

    private static readonly IReadOnlyDictionary<string, string> ShaderSets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["shaders_slang"] = "The Slang shaders RetroArch downloaded. Online Updater, Update Slang Shaders "
                + "downloads them again.",
            ["shaders_glsl"] = "The GLSL shaders RetroArch downloaded. Online Updater, Update GLSL Shaders "
                + "downloads them again.",
            ["shaders_cg"] = "The Cg shaders RetroArch downloaded. Online Updater, Update Cg Shaders downloads "
                + "them again.",
        };

    public RetroArchDownloadProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        RetroArchDiscovery? discovery = null)
        : base(environment, runner, inspector, scanner, discovery)
    {
    }

    public override string Id => "retroarch-downloads";

    public override string Name => "RetroArch shaders and game databases";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The shaders and game databases removed are gone from RetroArch until you download them again "
        + "from Online Updater, with Update Slang Shaders, Update GLSL Shaders, Update Cg Shaders and "
        + "Update Databases. Until then a shader preset that uses one does not load, and scanning for new "
        + "games finds none. Your saves, save states, playlists, cores, BIOS files, settings and the shader "
        + "presets you saved are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "RetroArch, the libretro frontend",
        Publisher = "the libretro project",
        Purpose = "RetroArch's online updater downloads its shader sets and its game databases into "
            + "folders beside the program, and nothing ever removes them.",
        Recommendation = "Remove these if you do not use RetroArch's shaders, or to reclaim the space "
            + "until you next want them. Deguffer finds RetroArch where Steam installed it. For any other "
            + "copy, add its folder under Emulator folders in Settings.",
    };

    private protected override string Taken => "downloaded shaders and game databases";

    private protected override void Read(RetroArchInstall install, RetroArchReading reading, CancellationToken ct)
    {
        if (Locate(install, RetroArchFolderSetting.Shaders, reading, "shaders") is { } shaders)
        {
            ReadShaders(install, shaders, reading, ct);
        }

        if (Locate(install, RetroArchFolderSetting.Database, reading, "game databases") is { } database)
        {
            ReadDatabase(install, database, reading, ct);
        }
    }

    private static void ReadShaders(RetroArchInstall install, string folder, RetroArchReading reading, CancellationToken ct)
    {
        if (reading.Entries(folder) is not { } entries)
        {
            return;
        }

        reading.Survivors.Add((folder, "RetroArch's shader folder, with the presets you saved. Only the "
            + "shader sets its online updater downloads are removed from it."));

        var recognised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                reading.DeclineLink(path);
            }
            else if (entry is DirectoryInfo && ShaderSets.TryGetValue(entry.Name, out var reason))
            {
                recognised.Add(entry.Name);
                reading.Offer(path, reason, TargetKind.Directory, ShaderGroup);
            }
            else
            {
                reading.Keep(
                    path,
                    "Not one of the shader sets RetroArch's online updater downloads, so it is left alone.",
                    tell: entry is DirectoryInfo);
            }
        }

        reading.Folders.Add(new RetroArchFolderReading(TopOf(install, folder), folder, recognised));
    }

    private static void ReadDatabase(RetroArchInstall install, string folder, RetroArchReading reading, CancellationToken ct)
    {
        if (reading.Entries(folder) is not { } entries)
        {
            return;
        }

        reading.Survivors.Add((folder, "RetroArch's game database folder. Only the databases its online "
            + "updater downloads are removed from it."));

        var recognised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                reading.DeclineLink(path);
            }
            else if (entry is FileInfo && entry.Name.EndsWith(DatabaseExtension, StringComparison.OrdinalIgnoreCase))
            {
                recognised.Add(entry.Name);
                reading.Offer(
                    path,
                    $"The game database for {Path.GetFileNameWithoutExtension(entry.Name)}, which RetroArch "
                    + "downloaded. Online Updater, Update Databases downloads it again.",
                    TargetKind.File,
                    DatabaseGroup);
            }
            else
            {
                reading.Keep(
                    path,
                    "Not a game database RetroArch's online updater downloads, so it is left alone.",
                    tell: entry is DirectoryInfo);
            }
        }

        reading.Folders.Add(new RetroArchFolderReading(TopOf(install, folder), folder, recognised));
    }
}
