using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The box art, title screens, snapshots and logos RetroArch keeps for the games in its playlists,
/// which reach many gigabytes on a machine with a real library.
///
/// <para><b>Tier 3, although most of it downloads again.</b> The Playlist Thumbnails Updater, and
/// RetroArch itself as each game is shown, fetch the pictures the libretro server carries. A picture
/// the user added by hand, for a game the server does not carry or in place of the one it does, sits
/// in the same folder under the same kind of name, and nothing tells it apart. It is lost for good, so
/// the row says so and is never ticked for the user.</para>
///
/// <para><b>Only the four kinds of picture, in each system's folder.</b> RetroArch writes a thumbnail to
/// <c>&lt;thumbnails&gt;\&lt;system&gt;\Named_Boxarts</c>, <c>Named_Snaps</c>, <c>Named_Titles</c> or
/// <c>Named_Logos</c>. Those folders are the targets, and the system folders, the thumbnails folder
/// and everything else in them survive. The avatars and achievement badges RetroArch keeps in the
/// thumbnails folder are not thumbnails, and are left alone.</para>
/// </summary>
public sealed class RetroArchThumbnailProvider : RetroArchProviderBase
{
    private static readonly IReadOnlyDictionary<string, string> Kinds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Named_Boxarts"] = "box art",
            ["Named_Snaps"] = "snapshots",
            ["Named_Titles"] = "title screens",
            ["Named_Logos"] = "logos",
        };

    /// <summary>What RetroArch keeps in the thumbnails folder that is not a system's thumbnails.</summary>
    private static readonly IReadOnlyDictionary<string, string> NotThumbnails =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["discord"] = "The Discord avatars RetroArch keeps. They are not thumbnails, so they are left alone.",
            ["cheevos"] = "The achievement badges RetroArch keeps. They are not thumbnails, so they are left alone.",
        };

    public RetroArchThumbnailProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        RetroArchDiscovery? discovery = null)
        : base(environment, runner, inspector, scanner, discovery)
    {
    }

    public override string Id => "retroarch-thumbnails";

    public override string Name => "RetroArch thumbnails";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override string WhatHappensOnNextUse =>
        "RetroArch shows no box art, title screens or snapshots for those games until they are downloaded "
        + "again, with Online Updater, Playlist Thumbnails Updater, or as each game is shown. A picture you "
        + "added yourself, or one for a game the libretro server does not carry, is gone permanently: "
        + "nothing tells it apart from a downloaded one. Your games, playlists and saves are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "RetroArch, the libretro frontend",
        Publisher = "the libretro project",
        Purpose = "RetroArch keeps a picture of each game in its playlists: box art, a title screen, a "
            + "snapshot and a logo. It downloads most of them from the libretro server, and you can add "
            + "your own.",
        Recommendation = "Remove a system's thumbnails only if you added no pictures of your own to it, "
            + "because nothing tells yours apart from downloaded ones. Deguffer finds RetroArch where "
            + "Steam installed it. For any other copy, add its folder under Emulator folders in Settings.",
    };

    private protected override string Taken => "thumbnails";

    private protected override void Read(RetroArchInstall install, RetroArchReading reading, CancellationToken ct)
    {
        if (Locate(install, RetroArchFolderSetting.Thumbnails, reading, "thumbnails") is not { } folder
            || reading.Entries(folder) is not { } systems)
        {
            return;
        }

        reading.Survivors.Add((folder, "RetroArch's thumbnails folder. Only the pictures in each system's "
            + "folder are removed from it."));

        var top = TopOf(install, folder);
        var recognised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var system in systems)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(system.FullName);

            if (system.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                reading.DeclineLink(path);
            }
            else if (system is not DirectoryInfo)
            {
                reading.Keep(path, "Not a system's thumbnails, so it is left alone.", tell: false);
            }
            else if (NotThumbnails.TryGetValue(system.Name, out var reason))
            {
                reading.Keep(path, reason, tell: false);
            }
            else if (ReadSystem(top, path, system.Name, reading, ct))
            {
                recognised.Add(system.Name);
            }
        }

        reading.Folders.Add(new RetroArchFolderReading(top, folder, recognised));
    }

    /// <returns>Whether anything was offered from the system's folder.</returns>
    private static bool ReadSystem(string top, string folder, string system, RetroArchReading reading, CancellationToken ct)
    {
        if (reading.Entries(folder) is not { } entries)
        {
            return false;
        }

        reading.Survivors.Add((folder, $"The folder for {system}'s thumbnails. Only the pictures RetroArch "
            + "keeps in it are removed."));

        var recognised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(entry.FullName);

            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                reading.DeclineLink(path);
            }
            else if (entry is DirectoryInfo && Kinds.TryGetValue(entry.Name, out var kind))
            {
                recognised.Add(entry.Name);
                reading.Offer(
                    path,
                    $"The {kind} RetroArch keeps for {system}'s games. It downloads again what the libretro "
                    + "server carries, and anything you added yourself is gone.",
                    TargetKind.Directory,
                    system);
            }
            else
            {
                reading.Keep(
                    path,
                    "Not one of the kinds of picture RetroArch keeps for a system, so it is left alone.",
                    tell: entry is DirectoryInfo);
            }
        }

        // A system with nothing offered is not on Explore's way down, so its folder is refused whole.
        if (recognised.Count == 0)
        {
            return false;
        }

        reading.Folders.Add(new RetroArchFolderReading(top, folder, recognised));
        return true;
    }
}
