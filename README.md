<img src="assets/banner.svg" alt="" width="600">

# Deguffer

Deguffer finds what is filling your Windows drive and helps you get the space back without losing
anything you meant to keep. It recognises the caches, build output, downloads and leftovers that
applications and Windows leave behind, tells you what each one is and what deleting it would cost
you, and leaves the decision with you. It can also map a whole drive, find duplicate files, tidy
the installed programs list, and show where your memory is going.

*Guff* is British slang for rubbish, the stuff that piles up and serves no purpose. Deguffer
removes it.

Deguffer runs on Windows 10 (version 1809 or later) and Windows 11, and it is free and open source
under the MIT licence. It is at version 0.70.0. There is no published release yet, so for now you
will need to [build it from source](#building).

## What it does

- **Storage** checks 82 sources of waste, from package-manager and browser caches to `Windows.old`
  and old restore points, and sorts each one into a [safety tier](#safety-tiers). You tick what
  goes, and after the clean Deguffer checks that everything it promised to leave alone is still
  there.
- **Explore** draws a drive or a folder as a treemap, an icicle or a sunburst, or as a list of the
  largest files at any depth. It shows what grew since an earlier scan and what kinds of file take
  the space, and it can send what you pick to the Recycle Bin.
- **Duplicates** finds files stored more than once, matched by name, size, date or content. Before
  it removes a copy you marked, it compares that copy byte for byte with the one you are keeping.
- **Installed apps** finds entries in Windows' installed programs list for programs that are
  provably gone, backs each entry up before removing it, and runs a program's own uninstaller when
  you ask it to.
- **Memory** shows where physical memory goes, by application, by service and to Windows itself,
  and can ask one program you choose to close.

Deguffer starts without administrator rights. On the pages that can use them, a *Reopen as
administrator* button lets it read a drive's file table directly, which makes a scan much faster,
and run the Windows cleanup steps that need those rights.

## Why it exists

Windows' Disk Cleanup and Storage Sense know about Windows' own caches and little else. On a
working machine most of the waste is in application and package caches, downloaded toolchains and
runtimes, editor state, container images and search indexes, and each of those needs its own
knowledge to clear safely.

Deguffer began as an audit of a Windows 11 workstation that was down to 5.6 GB free on a 330 GB
system drive. Clearing three package-manager caches in the way each tool intends recovered 22.9 GB
in a few minutes, without touching any user data. The same audit nearly deleted 11 GB of VS Code
`workspaceStorage`, which looks like a cache but mostly holds AI chat history. Nothing in its path,
size or name gives that away, and a cleaner that ranks folders by size would have recommended
deleting it. That near miss is why Deguffer classifies everything before it offers it.

## Safety tiers

| Tier | What it is | Deleting it costs | Default |
| --- | --- | --- | --- |
| **1. Regenerable cache** | Whatever made it re-creates it when needed | A slower next use | Offered and pre-selected |
| **2. Regenerable, with cost** | Re-created by downloading or rebuilding gigabytes | Time and bandwidth | Offered, not pre-selected |
| **3. User data** | Logs, histories, saved sessions. It *looks* like cache | It is gone for good | Offered, never pre-selected |
| **4. Do not touch** | Settings, credentials, live state | Breakage | Never shown |

Anything Deguffer does not positively recognise is Tier 4, so it is left alone by default rather
than because someone remembered to exclude it. Outlook's `.ost` and `.pst` files are also named,
because they can be saved anywhere, and Deguffer will never remove one by any route.

## The rules it follows

- Where a tool has its own command for clearing its cache, Deguffer uses it.
  `dotnet nuget locals all --clear` cleared four locations, two of them outside `.nuget`, and a
  cleaner working from paths would have missed about 3 GB.
- It never deletes a tool's root folder, only the children it recognises. `.gradle` keeps
  disposable `caches` and `wrapper` folders next to `gradle.properties`, which can hold signing
  keys.
- Nothing is deleted until a scan has shown you what it is, and you can read the exact steps for
  each row before you agree to them.
- After each clean it checks that the tool's root folder, anything it did not recognise, and
  anything in Tier 4 are still there, and it names any check that failed.
- A file that Windows has locked is skipped, never forced.
- Every path is handled in Windows' long form. NuGet and Node trees often run past the 260-character
  limit, and a path cut short there would mean a partial deletion that nobody notices.
- Something you put on the keep list, such as a Playwright browser build or an older version of an
  application, is left out of every clean until you release it in Settings.
- A Storage clean deletes outright and cannot be undone. Explore and Duplicates use the Recycle Bin
  by default, and they will keep an item where it is, and tell you why, if the bin cannot take it
  and Windows would delete it permanently instead.
- A clean can be set to close Deguffer, or to lock, log off, sleep, hibernate, restart or shut down
  the PC, when it finishes. Deguffer waits
  30 seconds first so that you can cancel, does nothing if the clean's checks failed, and never
  forces a program to close.
- The Memory page is not a RAM cleaner. It never trims a program's memory or flushes Windows'
  memory lists, never calls a process idle or safe to close, and never touches a service.

## What Storage cleans

Each of the 82 sources has its own provider, which knows what that location is and what sits
beside it. A provider for a tool you do not have will say it is not installed. Build output such as
`node_modules`, `obj` and Rust's `target` folders is searched for only in the source folders you add
in Settings. [What Deguffer cleans, and what it costs you](docs/cache-locations.md) explains most of
these sources in detail.

<details>
<summary><b>Tier 1: regenerable caches</b></summary>

| Source | Notes |
| --- | --- |
| NuGet package cache | Cleared with `dotnet nuget locals all --clear`, not by path |
| npm package cache | Cleared with `npm cache clean --force` |
| pnpm store | |
| Gradle build cache | `caches` and `wrapper` only, never the Gradle user home itself: `.gradle`, or wherever `GRADLE_USER_HOME` points |
| Cargo crate cache | |
| Go build and module caches | |
| Zig build cache | Never the packages Zig fetched. What it built goes only together with the records that point at it |
| pip package cache | |
| uv package cache | |
| Poetry package cache | `poetry cache clear` per repository; never its `virtualenvs` |
| .NET intermediate build output | `obj` directories under your own source trees |
| Dart analysis server cache | |
| Roslyn solution index cache | One dated row per set of indexes, so the sets a program left behind when it moved folder can go; never the rest of Visual Studio's folder |
| VS Code editor caches | |
| VS Code C/C++ IntelliSense cache | |
| Chromium application caches | Chrome, Edge, Brave, Vivaldi and Opera, the Steam client's and the Battle.net launcher's built-in browsers, and every application that embeds the same engine, WebView2 applications included. Never their sign-ins, saved passwords or payment cards |
| Firefox caches | |
| Steam HTTP cache | The client's own cache beside the program; never your games, your cloud saves, or the sign-in kept by its built-in browser |
| Spotify streaming cache | Never the music and podcasts you downloaded, wherever Spotify's settings say they are |
| Plex Media Server transcoder files | `Transcode\Sessions` and the resized pictures; never anything written in the last 24 hours, the sync queue beside it, or the downloads folder |
| Jellyfin transcoder files | Only a folder Jellyfin's own marker shows is Jellyfin's; never anything written in the last 24 hours |
| Emby Server transcoder files | Only a `transcoding-temp` folder Emby made; never anything written in the last 24 hours |
| Epic Games launcher web cache | |
| Epic Games launcher store artwork | Machine-wide, under `%PROGRAMDATA%`, and shared by every account |
| Battle.net launcher cache | Never the launcher's account data, its database, or anything under `%PROGRAMDATA%` |
| GPU shader caches | |
| Squirrel updater leftovers | Staging directories an interrupted update left behind |
| Tool caches in temporary folders | Node's compile cache, and what Flutter, Dart's test runner, Firefox and Roslyn left there, each recognised by its own name and left alone while its tool may be using it |
| Claude Code session leftovers | Only what sessions and editors that have ended left behind; never a conversation, its memory or your sign-in |
| Claude Code command snapshots | The project snapshots Claude Code fails to remove when it exits; only those made before every running session started, or that Claude Code began to remove |
| Test browser profiles | The profiles Playwright and Puppeteer leave in a temporary folder when a test run is stopped; never one Deguffer can see a running browser using |
| Delivery Optimization cache | Windows' own command clears it, sized by Windows' own figure; never a file Windows was told to keep, and never Windows Update's own folder |
</details>

<details>
<summary><b>Tier 2: regenerable, with cost</b></summary>

| Source | Notes |
| --- | --- |
| Maven local repository | |
| Conda package cache | |
| Chromium offline site storage | What sites' service workers stored so they work offline, in the same browsers and applications as the Chromium caches row; a web application that worked offline does not do so again until it is next opened online |
| vcpkg build caches | |
| PlatformIO cache and unused packages | PlatformIO's own prune decides which installed packages nothing still needs |
| Affinity machine-learning models | The `modelcache` beside your asset library, never the version folder holding both |
| DaVinci Resolve render cache | Each project's render files in a `CacheClip` folder at the root of a drive or in your Videos folder; never optimised media, proxies, backups or recordings, and nothing while Resolve is running. Resolve renders it again only while your media is connected |
| After Effects disk cache | Each version's cache named for this computer, only in the folder After Effects' own preferences name; never anything beside it or above it, and nothing while After Effects is running. After Effects renders the frames again as you preview |
| Adobe media cache | The converted audio, waveforms and database Premiere Pro, After Effects, Audition and Media Encoder share, wherever Adobe's settings put them; never your LUTs, templates or Team Projects' auto-saves, and nothing while an Adobe application is running. Each project opens slowly once while Adobe converts its audio again |
| Capture One previews and thumbnails | The `Cache` in each catalog and session Capture One lists, wherever it is; never the photographs or the adjustments beside it. A catalog whose originals are offline cannot be browsed until they are reconnected |
| Playwright browsers | |
| Puppeteer browsers | The builds in `.cache\puppeteer`, never the rest of `.cache`; never a build a running browser is using, or the metadata Puppeteer finds its builds through |
| LM Studio superseded runtimes | Every llama.cpp build LM Studio has downloaded except the one it uses and the newest for each kind of hardware, removed by LM Studio's own command. LM Studio must be open, and it frees the space the next time it starts |
| Azure Functions Core Tools releases | |
| Graphics driver installer files | What NVIDIA's and AMD's installers unpacked or downloaded and left behind; never AMD's chipset install source or the NVIDIA app's update store |
| Autodesk installer files | What Autodesk's installers extracted into `C:\Autodesk` and left behind; never the network licence server, an administrator's deployment images or Autodesk Access's updates. A repair or an update then downloads the product again |
| Steam library artwork | One item per game, fetched again when Steam next shows it; never Steam's index of it. A picture you replaced by hand is lost, so keep that game |
| Steam shader pre-cache | One item per game, in every library Steam's own list names; never the games beside it |
| Emulator shader caches | What Cemu, RPCS3, Dolphin and PCSX2 compile for your GPU, found where each keeps its data and in the emulator folders you add in Settings; never Cemu's transferable cache, saves, memory cards, firmware or Dolphin's own `Shaders`, and nothing while the emulator runs |
| RetroArch shaders and game databases | The shader sets and `.rdb` databases its Online Updater downloaded, found through Steam's record and the emulator folders you add in Settings, in the folders its own `retroarch.cfg` names; never the presets you saved, `system`, cores, cheats, overlays or saves, and nothing while RetroArch runs |
| Unreal Engine derived data cache | The cache every project shares, in both the older folder and each Zen store; a store is left alone while its server runs |
| Node.js project dependencies | `node_modules` under your own source trees |
| Python virtual environments | |
| Rust build output | `target` directories under your own source trees |
| Unity project library | |
| Unreal project intermediate files | `Intermediate` beside a `.uproject` under your own source trees; never `Saved` or `Binaries` |
| Unreal project derived data cache | A project's own `DerivedDataCache` under your own source trees |
| Superseded application versions | Older versions a Squirrel-updated app still keeps |
| Temporary files | Emptied in place, taking only what nothing has touched for seven days; what a tool's own row offers is left to that row |
| Installer downloads in temporary folders | Updates VS Code, Docker Desktop and the Visual Studio Installer downloaded, and Blender's crashed sessions, only while the application is closed; never Blender's recovery files |
| Previous Windows installation | `Windows.old` and Setup's leftovers, removed by Windows' own Disk Cleanup handlers once the upgrade can no longer be undone and no update is unfinished |
| Leftover Windows update folders | `$WinREAgent` and `$GetCurrent`, which Microsoft does not document: offered on Deguffer's own stated judgement, whole, once nothing inside has changed for 30 days |
| Superseded driver packages | Older versions of drivers in Windows' driver store, removed by Windows' own Disk Cleanup handler; never the newest version of a driver, one a device is using, or one that is part of Windows |
| Superseded Windows components | The component store (`WinSxS`), reclaimed by `DISM /StartComponentCleanup` as administrator and sized by DISM's own analysis, which counts what the store shares with Windows once; nothing is ever deleted from the store |
| Local copies of cloud files | OneDrive's own "free up space": every file stays listed and opens while you are online. Files you keep on this device, and files with changes not yet uploaded, stay as they are |
</details>

<details>
<summary><b>Tier 3: user data, and anything else you cannot get back</b></summary>

| Source | Notes |
| --- | --- |
| Recycle Bin | |
| Windows update uninstall data | `DISM /StartComponentCleanup /ResetBase`, a row of its own: afterwards no update installed so far can be uninstalled |
| Older restore points | Every restore point but the newest, removed one at a time by System Restore itself as administrator; never the newest, a shadow copy that cannot be a restore point's, or System Protection's limit |
| Windows File History | Windows' own command drops saved versions past an age you set; the backup drive itself is never touched |
| Crash dumps and error reports | |
| Windows servicing logs | The logs a reset of this PC leaves are cleared by Windows' own cleanup for them, never by path |
| VS Code editor logs and crash reports | |
| Epic Games launcher logs and crash reports | |
| Battle.net launcher logs | |
| RetroArch thumbnails | Each system's box art, snapshots, title screens and logos, grouped by system. Most download again, but a picture you added yourself cannot be told apart and is lost |
| Claude Code MCP server logs | |
| Tool logs in temporary folders | The Remote Desktop client's traces, the Windows App's and ServiceHub's logs, and VS Code's updater logs; the folders they are written into stay |
| Claude Code rewind snapshots | One folder per session, dated by the folder and never by the snapshots in it; a running session's are never offered |
| WSL and Docker virtual disks | Reported, never acted on: each disk's size on the drive, whether it is sparse, what Docker says is reclaimable inside when Docker Desktop is already running, and the vendor's commands in the order to use them. Nothing is pruned, compacted or deleted |
| Claude Code conversations | One item per session, by its title and project, for when you want one gone before Claude Code deletes it itself after 30 days; never one running, one in a project Claude Code is running in, or one used this week |
</details>

## Explore

Explore reads the drive's file table when Deguffer runs as administrator, and walks the folders
when it does not. You can view a whole drive or one folder as a treemap, an icicle, a sunburst or a
list, and colour it by folder, by growth or by file type. The Files view lists the largest files at
any depth, filtered by name or wildcard, type, size, and how long it has been since each file was
written.

Two cards sit beside the map. *What grew* ranks folders by how much they grew since an earlier
scan, and it can replay the change on the map from then to now. *What kind of files* breaks the
folder down by type. If you point at a row in either card, its shapes will light up on the map.

The map pans and zooms with the mouse, a touchpad, a touch screen or the keyboard. It has Back and
Forward buttons like a web browser, and a small overview of the whole picture while you are zoomed
in. It follows Windows' animation and high contrast settings, and screen readers can read each
shape's name, size and share of its folder.

Explore will not remove the Windows and Program Files folders, other accounts' profiles, your own
profile and AppData folders themselves, a tool's root folder, or any folder that contains one of
these. When it refuses, it gives the reason on the item instead of greying out a button. After a
removal it checks that everything beside the removed item is still there.

## Duplicates

You choose the drives and folders to search and what two files must share to count as copies: the
name, the size, the last-modified time, the content, or any mix of these. Content is compared by a
checksum you choose, XXH128 by default. Checksums are remembered, so a second search only reads the
files that changed. A location can be set as a reference, so its files are matched but never marked
for removal.

Nothing is marked when a search finishes. You mark copies yourself or with a named rule, and every
group always keeps at least one copy on an internal drive, outside temporary and cloud folders. A
link is never followed, an empty file is never matched, and a file that is only in the cloud is
never read, so a search will not make Windows download anything. The results can be saved as a CSV
file.

## Installed apps

Over time Windows' list of installed programs collects entries for programs that are no longer
there. Deguffer removes an entry only when the machine proves the program is gone, for example
because Windows Installer says the product is not installed or its uninstaller file is missing. By
default it backs the entry up with `reg.exe` first. It removes only the registry key, never any
files, and a backup can be restored from the same page. For programs that are still installed, it runs each one's own
uninstaller, one at a time.

## Memory

Low free memory on Windows is normally a sign that Windows is working well. It fills memory that
nothing else needs with cache and hands it back as soon as something asks. The problems you do
notice come from committed memory reaching its limit, when programs can no longer allocate, and from
constant hard faults, which show as disk activity rather than in any memory figure. The Memory page
leads with committed memory, because one figure states it.

The page draws memory with the same treemap, icicle and sunburst as Explore, in three parts:
applications by process tree, services by the process that hosts them, and Windows itself,
including the memory no figure accounts for. Every size is a lower bound, and the page says where a
figure is missing instead of drawing it as zero.

Its one action is to ask a program you pick to close, in the same way as its own close button, so
any question about unsaved work comes from the program itself. Deguffer never ends a process,
closes a service host, or touches a program running under another account.

## Building

You will need Windows and the .NET 10 SDK. The Windows App SDK and Win2D come from NuGet.

```
dotnet build Deguffer.sln
dotnet test  Deguffer.sln
```

The app is written to `!Distribution\Deguffer.App.exe`. It is a WinUI 3 app that runs without
installation and includes its own .NET runtime, because a disk-cleanup tool is often needed on a
machine too full to install one.

`Deguffer.Core` holds every safety rule and has no UI dependency, so it is tested as an ordinary
class library. Adding a cache source means one new `ICleanupProvider` and its tests, and the safety
model then applies to it like every other source.

```
Deguffer.Core/        safety model, providers, planner and executor, Explore, Duplicates, Memory
Deguffer.Core.Tests/
Deguffer.Testing/     the fakes and fixtures the test projects share
Deguffer.App/         the WinUI 3 shell
Deguffer.App.Tests/
Deguffer.Benchmark/   times how a scan reads the disk, run by hand (see CONTRIBUTING.md)
Deguffer.Benchmark.Tests/
```

## Roadmap

Still to come are VS Code workspace storage, with each workspace's age, and the Android SDK.

Some things are left out on purpose. Deguffer will never delete files from `WinSxS`,
`Windows\Installer` or installers' package caches, because the ways that can go wrong are severe.
The component store is reduced only through DISM's own cleanup. The Memory page will not gain a
second action, and Duplicates will not replace copies with links.

## Documentation

- [What Deguffer cleans, and what it costs you](docs/cache-locations.md): each location in detail
- [Specification](docs/todo/_spec.md): the safety model and the evidence behind it
- [CONTRIBUTING.md](CONTRIBUTING.md): how to ask for a change. Pull requests are not accepted.
- [SECURITY.md](SECURITY.md): how to report a vulnerability privately

## Licence

[MIT](LICENSE).
