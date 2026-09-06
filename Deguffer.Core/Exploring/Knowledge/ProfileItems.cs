namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// The signed-in account's own profile: the folders a person keeps their work in, and the three
/// tiers of application data behind them.
///
/// <para>Two kinds of thing sit side by side in here and telling them apart is the whole value of
/// these entries. Some of it is the only copy of somebody's work, and some of it is a cache that
/// rebuilds itself in minutes — and by size alone a photo library and a browser cache look
/// identical. So every verdict here says which of the two it is before it says anything else.</para>
///
/// <para>The three AppData tiers are read through <see cref="Safety.IUserEnvironment"/> rather than
/// assembled from the profile path, because <c>%LOCALAPPDATA%</c> can be pointed somewhere else. A
/// path built by assumption would explain a folder that is not there and stay silent about the one
/// that is.</para>
/// </summary>
internal static class ProfileItems
{
    public static IReadOnlyList<KnownItem> All { get; } =
    [
        new(
            KnownPlace.UserProfile,
            string.Empty,
            "This account's own profile: its documents, its settings, its downloads and the data "
            + "every program it runs keeps for it. Nobody else signing in to this computer sees any "
            + "of it.",

            "Deguffer removes things from inside a profile and never the profile itself, which "
            + "holds everything it would otherwise offer to clean."),

        Data(
            "Desktop",
            "The files and shortcuts on the desktop. They are ordinary files kept in an ordinary "
            + "folder, which is why something large dragged onto the desktop and forgotten about "
            + "goes on taking up space."),

        Data(
            "Documents",
            "Where most programs save by default. Plenty of them also keep settings and saved games "
            + "in here alongside actual documents, so it is rarely only documents."),

        new(
            KnownPlace.UserProfile,
            "Downloads",
            "Where a browser puts what it downloads. Installers, disk images and archives collect "
            + "here and are almost never removed afterwards, which makes it one of the largest "
            + "folders in a profile and one of the easiest to reclaim.",

            "It is your own files rather than a cache, and Windows leaves it alone unless asked — "
            + "Storage Sense can be set to remove downloads left untouched for a chosen number of "
            + "days."),

        Data(
            "Pictures",
            "The default place for photographs and screenshots, with the camera roll and the "
            + "screenshot folder inside it. On a machine used for photographs it is often the "
            + "largest folder on the drive."),

        Data(
            "Music",
            "The default place for music files. Its size follows a collection rather than anything "
            + "the machine does on its own."),

        Data(
            "Videos",
            "The default place for video files. Video is the largest thing most people keep, so a "
            + "handful of files here can account for a substantial part of a drive."),

        Data(
            "Saved Games",
            "Where games are meant to keep their saves. Saves are small, irreplaceable, and often "
            + "not backed up anywhere."),

        Data(
            "Favorites",
            "Bookmarks kept as small shortcut files, from Internet Explorer originally and still "
            + "where some programs import from. It is a few kilobytes."),

        Data(
            "Links",
            "The shortcuts pinned in File Explorer's navigation pane. It is a few kilobytes."),

        Data(
            "Searches",
            "Saved search definitions, so a search can be run again without describing it afresh. "
            + "It is normally close to empty."),

        Data(
            "Contacts",
            "The Windows Contacts store, one small file per contact. Little on a modern machine "
            + "uses it."),

        Data(
            "3D Objects",
            "A folder Windows added for Paint 3D, which it no longer ships. It is almost always "
            + "empty."),

        new(
            KnownPlace.UserProfile,
            "OneDrive",
            "The folder OneDrive keeps in step with the cloud. With Files On-Demand switched on, "
            + "much of what is listed here is a placeholder rather than a file — the name and the "
            + "size are on the drive and the contents are not — so it can appear far larger than "
            + "the space it occupies.",

            "Deleting anything here deletes it from the cloud and from every other device signed "
            + "in, so the way to recover the space is 'Free up space' on the folder, which turns "
            + "local copies back into placeholders without removing anything."),

        new(
            KnownPlace.UserProfile,
            "AppData",
            "Where programs keep their settings and their working data for this account, divided "
            + "into three: Roaming for what should follow the account between machines, Local for "
            + "what belongs to this machine or is too large to carry, and LocalLow for programs "
            + "running with reduced privileges. On a machine used for development it is usually the "
            + "largest part of the profile.",

            "It is settings and application data rather than spare copies, so it does not go as a "
            + "whole — the caches inside it are what can be cleared, one at a time."),

        new(
            KnownPlace.RoamingAppData,
            string.Empty,
            "The application data meant to follow this account to another machine: settings, "
            + "profiles, custom dictionaries and the like. Well-behaved programs keep anything bulky "
            + "out of here, so it is usually the smallest of the three tiers.",

            "It holds settings rather than caches, and clearing it resets programs to their defaults "
            + "and signs some of them out."),

        new(
            KnownPlace.LocalAppData,
            string.Empty,
            "The application data that stays on this machine: browser caches, package caches, "
            + "per-account installed programs and the temporary folder. It does not follow the "
            + "account elsewhere, either because it describes this machine or because it is too "
            + "large to carry. This is where most of a developer's disk goes.",

            "The tier itself holds installed programs and settings as well as caches, so what can "
            + "be cleared is particular folders inside it rather than the whole thing."),

        new(
            KnownPlace.UserProfile,
            @"AppData\LocalLow",
            "Application data for programs running with reduced privileges, which cannot write to "
            + "the other two tiers. Browser sandboxes, some games and some graphics drivers use it. "
            + "It is usually small, with one exception worth knowing about: NVIDIA keeps a second "
            + "compiled shader cache here, and it can be as large as the one in Local.",

            "It holds settings as well as caches, so it does not go as a whole."),

        new(
            KnownPlace.LocalAppData,
            "Temp",
            "This account's temporary folder. Installers, compilers, test runners and browsers "
            + "scratch here and are supposed to clear up afterwards; a great many do not, so it "
            + "grows without limit and is frequently gigabytes.",

            "Disk Cleanup clears it and leaves the past week alone, because a newer file may belong "
            + "to something still running, and Storage Sense clears it too once it is switched on."),

        new(
            KnownPlace.LocalAppData,
            @"Microsoft\Windows\INetCache",
            "The web cache shared by Internet Explorer, Office and every desktop program that "
            + "fetches over the web through Windows itself, so it fills even on a machine where "
            + "nobody opens Internet Explorer. Windows caps it at fifty megabytes and evicts older "
            + "entries itself.",

            "Clearing it is harmless and recovers at most a few tens of megabytes, because Windows "
            + "is already keeping it that small."),

        new(
            KnownPlace.LocalAppData,
            @"Microsoft\Windows\WebCache",
            "A database indexing the web cache, browsing history and cookies for Internet Explorer "
            + "and the components other programs use through it. It does not shrink on its own, so "
            + "it can reach a few hundred megabytes.",

            "Windows holds it open while the account is signed in and it can only be cleared while "
            + "signed out, which loses that browsing history and nothing else."),

        new(
            KnownPlace.LocalAppData,
            "CrashDumps",
            "Memory dumps of programs that have crashed, written only where somebody has switched "
            + "the feature on. Windows keeps the ten most recent by default, but a full dump is a "
            + "copy of everything the failing program had in memory, so ten of them can be several "
            + "gigabytes.",

            "Nothing reads them back once the crash has been looked at, so deleting them recovers "
            + "their space outright."),

        new(
            KnownPlace.LocalAppData,
            "Packages",
            "One folder per installed Store or packaged app, holding that app's own data for this "
            + "account. Inside each, LocalState is the app's real data, LocalCache is derived but "
            + "may hold sign-in tokens, and TempState is scratch Windows may clear at any time.",

            "The folder for an app is not a cache — resetting an app through Settings, under Apps "
            + "and Advanced options, is what clears it properly and it loses that app's data."),

        new(
            KnownPlace.LocalAppData,
            @"Microsoft\Edge\User Data",
            "Microsoft Edge's profile: the cache of pages and images alongside the history, "
            + "bookmarks, cookies and saved passwords. Several gigabytes is ordinary, and most of "
            + "it is cache.",

            "Deleting the folder signs the account out everywhere and loses its bookmarks, so "
            + "Edge's own 'Clear browsing data' is what drops the cache while keeping the rest."),

        new(
            KnownPlace.LocalAppData,
            @"Google\Chrome\User Data",
            "Google Chrome's profile, arranged the same way as Edge's: the cache of pages and "
            + "images sits in the same folder as the history, bookmarks, cookies and saved "
            + "passwords. Several gigabytes is ordinary.",

            "Deleting the folder signs the account out everywhere and loses its bookmarks, so "
            + "Chrome's own 'Clear browsing data' is what drops the cache while keeping the rest."),

        new(
            KnownPlace.LocalAppData,
            "Programs",
            "Where software that installs without administrator rights puts itself — Visual Studio "
            + "Code, Teams, and a good deal else. It is installed software rather than data, so it "
            + "does not shrink and can be several gigabytes.",

            "Deleting a folder here removes a program without unregistering it, leaving broken "
            + "shortcuts behind, so uninstall it through Settings under Apps instead."),

        new(
            KnownPlace.LocalAppData,
            @"Microsoft\OneDrive",
            "The OneDrive program itself, not the files it synchronises. Each update installs "
            + "beside the last, so several versions accumulate and a gigabyte or two is normal.",

            "Deleting it breaks synchronising while leaving OneDrive registered as installed, and "
            + "there is no supported way to prune the older versions."),

        new(
            KnownPlace.UserProfile,
            "NTUSER.DAT",
            "The registry for this account: every per-user setting Windows and the programs on it "
            + "have. Windows holds it open the whole time the account is signed in, and the small "
            + "files beside it are its transaction logs.",

            "It cannot be deleted while signed in, and deleting it otherwise loses every setting "
            + "and can leave the account unable to sign in properly."),

        new(
            KnownPlace.UserProfile,
            ".ssh",
            "The private keys and settings for connecting to other machines over SSH, and for "
            + "pushing to a source repository. A private key here is the equivalent of a password. "
            + "It is a few kilobytes.",

            "It cannot be replaced if it goes — losing a private key means generating a new one and "
            + "registering it with every machine that trusted the old one."),

        new(
            KnownPlace.UserProfile,
            ".gitconfig",
            "Git's settings for this account: the name and address commits are made under, the "
            + "aliases, and how credentials are stored. It is a few kilobytes and may hold a token.",

            "There is nothing to recover, and deleting it silently changes the identity commits are "
            + "made under."),
    ];

    /// <summary>
    /// One of the profile's own folders. Everything here is the person's own work rather than
    /// anything a program can rebuild, and the verdict says the same thing for all of them — which
    /// is exactly why it is written once.
    /// </summary>
    private static KnownItem Data(string name, string summary) =>
        new(
            KnownPlace.UserProfile,
            name,
            summary,
            "This is your own content rather than anything a program keeps a spare copy of, so "
            + "whatever is deleted here is gone for good.");
}
