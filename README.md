<img src="assets/banner.svg" alt="" width="600">

# Deguffer

A Windows utility that finds and reclaims wasted disk space, with a safety model good enough to
trust. It knows what specific locations on your disk actually are, and tells you what each one
costs to lose, so you decide what goes. It also shows you where your memory goes, with the same
refusal to guess on your behalf, and without claiming to free a byte of it.

**Guff** is British for nonsense, waffle, rubbish — the stuff that accumulates and serves no
purpose. **De-** removes it.

> **Status:** Version 0.70.0. Fifty-nine sources across the tiers, a file-table-backed Explore view
> of the whole drive, and a Memory view of where physical memory goes that can ask one program you
> pick to close itself. See [Roadmap](#roadmap).

## Why

Windows' own Disk Cleanup and Storage Sense understand Windows' caches, and stop there. Everything
else that quietly fills a drive — application and package caches, downloaded toolchains and
runtimes, per-workspace editor state, container images, search and index databases — is invisible
to them. Those are the bulk of the waste, and each one needs its own knowledge to clear safely.

Size alone cannot tell you which is which, so Deguffer does not rank folders by size and let you
guess. It recognises specific locations, says what each holds and what losing it costs, and leaves
the decision with you.

On the workstation this tool was designed against — Windows 11, a ~330 GB system drive, down to
**5.6 GB free** — targeted cleanup of three package-manager caches recovered **22.9 GB** in a few
minutes without touching a single piece of user data. That audit is what the safety model was built
from.

Scanning that same drive today, version 0.70.0 recognises 34 locations and finds 29 of them
present. It offers **10.3 GB** of Tier 1 cache pre-selected and **1.4 GB** of Tier 2 beside it, and
reports **6.4 GB** of Tier 3 user data separately with nothing pre-selected. The Recycle Bin and the
crash dumps are real space, and they are still yours to decide about.

Memory is the other thing a Windows machine runs short of, and finding out where yours went should
not mean leaving for a second program. The subject holds the same trap in a different form. Low free
memory is Windows working as designed: it fills memory nothing else needs with cache, and hands
those pages back the moment something asks for them. The two failures you do feel are commit charge
reaching the commit limit, when allocations start to fail, and sustained hard faulting, which shows
in disk activity rather than in any memory figure. So Deguffer draws where physical memory goes,
leads with the one of the two that a single figure states, and frees nothing.

## The idea: safety tiers

Sizes are easy to compute. The classification is the part that takes knowledge, and it is the
product.

| Tier | Meaning | Deleting it costs | Default |
| --- | --- | --- | --- |
| **1 — Regenerable cache** | Whatever made it re-creates it on demand | A slower next use | Offered, pre-selected |
| **2 — Regenerable, with cost** | Re-created by re-downloading gigabytes | Time and bandwidth | Offered, not pre-selected |
| **3 — User data** | Logs, histories, saved sessions. *Looks* like cache | **Gone permanently** | Never pre-selected |
| **4 — Do not touch** | Config, credentials, live state | Breakage | Not shown at all |

### The mistake this exists to prevent

During the original audit, ~11 GB of VS Code `workspaceStorage` was initially classified as cache.
It is mostly **AI chat session history** — a permanent record of past conversations, sitting in a
directory whose name and location strongly suggest "cache".

Nothing about its path, size or shape distinguishes it from genuinely disposable state. Only
knowing what the subfolder *contains* does. A size-ranked directory list would have recommended
deleting it, and the user would have silently lost months of history. Tier 3 exists because that
class of error is invisible until it is irreversible.

## Rules the design is built on

- **Prefer a tool's own eviction command over deleting paths.** `dotnet nuget locals all --clear`
  cleared four locations, two of which were not under `.nuget` at all. A path-based cleaner would
  have missed ~3 GB.
- **Never delete a tool's root directory.** `~\.gradle` holds `caches` and `wrapper` (disposable)
  next to `gradle.properties`, which may contain signing keys. Only recognised children are ever
  targeted; anything unrecognised is Tier 4 by construction.
- **Nothing is deleted until you have seen what it is.** Scanning is the primary action; cleaning
  is a separate, explicit step.
- **Verify the negative.** After acting, assert that the things that should have survived did —
  config files, protected directories — and report it. This turns "I think it worked" into
  evidence, and catches an over-broad rule on the first run rather than the hundredth.
- **What you keep stays kept.** An item Deguffer can recognise wherever it is stored (a Playwright
  browser build, an Azure Functions Core Tools release, a superseded application version) can be put
  on a keep list from its row's list of items. A kept item is left out of every clean, checked by
  every clean to be still there, and listed on the Settings page until you release it. The list is
  stored apart from the remembered selection, because a lost entry only offers something again,
  where a misread tick would pre-select it.
- **A locked file is the OS protecting live state.** Access-denied is skipped, not escalated.
- **Not a RAM cleaner, in any form.** The Memory view never trims a working set, and never purges or
  flushes the standby list or any other memory list. Those are the tricks that make a free-memory
  figure rise, and the pages they push out come back as page faults, read from the disk a second
  time. It never says that a process, a service or a part of Windows is idle or safe to close, and
  it never starts, stops, pauses or reconfigures a service.
- **Long paths are mandatory.** NuGet and Node trees routinely exceed `MAX_PATH`, and truncating
  there is the likeliest cause of a silent partial deletion.

## What it handles today

Fifty-nine providers, each holding its own knowledge of one location. A provider reports "not
installed" cleanly on a machine without that toolchain.

**Tier 1 — regenerable cache.** Whatever wrote it re-creates it on demand.

| Source | Notes |
| --- | --- |
| NuGet package cache | Cleared with `dotnet nuget locals all --clear`, not by path |
| npm package cache | Cleared with `npm cache clean --force` |
| pnpm store | |
| Gradle build cache | `caches` and `wrapper` only, never the `.gradle` root |
| Cargo crate cache | |
| Go build and module caches | |
| pip package cache | |
| uv package cache | |
| Poetry package cache | `poetry cache clear` per repository; never its `virtualenvs` |
| .NET intermediate build output | `obj` directories under your own source trees |
| Dart analysis server cache | |
| Roslyn solution index cache | One dated row per set of indexes, so the sets a program left behind when it moved folder can go; never the rest of Visual Studio's folder |
| VS Code editor caches | |
| VS Code C/C++ IntelliSense cache | |
| Chromium application caches | Chrome, Edge, Brave, Vivaldi and Opera, the Battle.net launcher's built-in browser, and every application that embeds the same engine |
| Firefox caches | |
| Steam web cache | |
| Spotify streaming cache | Never the music and podcasts you downloaded, wherever Spotify's settings say they are |
| Plex Media Server transcoder files | `Transcode\Sessions` and the resized pictures; never anything written in the last 24 hours, the sync queue beside it, or the downloads folder |
| Jellyfin transcoder files | Only a folder Jellyfin's own marker shows is Jellyfin's; never anything written in the last 24 hours |
| Emby Server transcoder files | Only a `transcoding-temp` folder Emby made; never anything written in the last 24 hours |
| DaVinci Resolve render cache | Each project's render files in a `CacheClip` folder at the root of a drive or in your Videos folder; never optimised media, proxies, backups or recordings, and nothing while Resolve is running |
| Epic Games launcher web cache | |
| Epic Games launcher store artwork | Machine-wide, under `%PROGRAMDATA%`, and shared by every account |
| Battle.net launcher cache | Never the launcher's account data, its database, or anything under `%PROGRAMDATA%` |
| GPU shader caches | |
| Squirrel updater leftovers | Staging directories an interrupted update left behind |
| Tool caches in temporary folders | Node's compile cache, and what Flutter, Dart's test runner, Firefox and Roslyn left there, each recognised by its own name and left alone while its tool may be using it |
| Claude Code session leftovers | Only what sessions and editors that have ended left behind; never a conversation, its memory or your sign-in |
| Test browser profiles | The profiles Playwright and Puppeteer leave in a temporary folder when a test run is stopped; never one Deguffer can see a running browser using |

**Tier 2 — regenerable, with cost.** Re-created by re-downloading or rebuilding.

| Source | Notes |
| --- | --- |
| Maven local repository | |
| Conda package cache | |
| vcpkg build caches | |
| PlatformIO cache and unused packages | PlatformIO's own prune decides which installed packages nothing still needs |
| Affinity machine-learning models | The `modelcache` beside your asset library, never the version folder holding both |
| Capture One previews and thumbnails | The `Cache` in each catalog and session Capture One lists, wherever it is; never the photographs or the adjustments beside it. A catalog whose originals are offline cannot be browsed until they are reconnected |
| Playwright browsers | |
| Azure Functions Core Tools releases | |
| Graphics driver installer files | What NVIDIA's and AMD's installers unpacked or downloaded and left behind; never AMD's chipset install source or the NVIDIA app's update store |
| Steam library artwork | One item per game, fetched again when Steam next shows it; never Steam's index of it. A picture you replaced by hand is lost, so keep that game |
| Steam shader pre-cache | One item per game, in every library Steam's own list names; never the games beside it |
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
| Local copies of cloud files | OneDrive's own "free up space": every file stays listed and opens while you are online. Files you keep on this device, and files with changes not yet uploaded, stay as they are |

**Tier 3 — user data.** Never pre-selected, and shown with what losing it costs.

| Source | Notes |
| --- | --- |
| Recycle Bin | |
| Windows File History | Windows' own command drops saved versions past an age you set; the backup drive itself is never touched |
| Crash dumps and error reports | |
| Windows servicing logs | The logs a reset of this PC leaves are cleared by Windows' own cleanup for them, never by path |
| VS Code editor logs and crash reports | |
| Epic Games launcher logs and crash reports | |
| Battle.net launcher logs | |
| Claude Code MCP server logs | |
| Tool logs in temporary folders | The Remote Desktop client's traces, the Windows App's and ServiceHub's logs, and VS Code's updater logs; the folders they are written into stay |
| Claude Code rewind snapshots | One folder per session, dated by the folder and never by the snapshots in it; a running session's are never offered |

**Tier 4** is not a list of sources. It is everything a provider does not recognise, which is
excluded by construction rather than by enumeration. Outlook's mailbox and data files are named as
well, because they can be saved anywhere: Deguffer never removes an `.ost` or a `.pst`, by any route,
wherever it finds one.

## Where the memory goes

The Memory view draws physical memory through the same treemap, icicle and sunburst Explore uses,
with a plain list beside them. The headline is committed memory against the commit limit, and
available memory sits next to it rather than above it: a headline led by memory "in use" would
present a cache as a problem.

The picture has three parts. **Applications** follows the process tree, and a parent link counts
only where the creation times allow it, because Windows reuses process identifiers. **Services** is
grouped by host process, and a host carries the name of its service where it holds one and a count
where it holds several, because memory in a shared host cannot be divided between them. **Windows**
holds the compression store, the non-paged pool, the memory lists, and the memory no figure
attributes — drawn and labelled rather than hidden, because a reader takes a missing remainder for
a leak. Nothing is drawn twice: the paged pool sits inside the system working set, so it is not a
part of its own, and the page says where its pages are counted.

Every size is a lower bound, and the page says so in its own words. The private working set is the
closest single figure to what closing a program would return, and compressed pages, shared pages and
the memory the kernel holds on a process's behalf are not in it. Where a figure cannot be trusted or
a table cannot be read to its end, the view says which one and what is missing, rather than drawing
a zero.

The view has one action, and it will not grow a second. Pick a program and Deguffer will ask it to
close, the way its own close button does: it posts one close to each of that program's own windows
and does nothing else, so unsaved work stays the program's own question, asked in the program's own
words. It never terminates anything, never closes a service host, never touches a program in another
session or under another account, and never asks for more rights in order to reach one. Whatever it
will not do, it says why on the row rather than greying a button out, and it confirms every close
before sending it, naming the program and how many windows will be asked. Afterwards it reports what
it sent and where, what it expected to exit, and what exited that it sent nothing to — and it never
claims that nothing else stopped, because processes exit on their own and it cannot know that.

## Building

Requires the **.NET 10 SDK**. `Deguffer.App` additionally needs the Windows App SDK workload.

```
dotnet build Deguffer.sln
dotnet test  Deguffer.sln
```

`Deguffer.Core` carries no UI dependency and is testable as an ordinary class library. The app is
WinUI 3, unpackaged, shipped self-contained — a disk-cleanup tool is exactly what someone reaches
for on a machine too full to install a runtime.

## Architecture

```
Deguffer.Core/
  Safety/        tier classification, disposable-child rules, long paths, machine seams
  Scanning/      size aggregation, free space
  Execution/     plan model, planner, executor, post-run verification
  Providers/     one class per known cache
  Exploring/     whole-drive view: file-table reads, tree building, what each location is
  Memory/        where memory goes: process and service tables, checked figures, the memory tree
  Configuration/ user preferences
  Diagnostics/   run logging
Deguffer.Core.Tests/
Deguffer.App/  WinUI 3 shell, MVVM over Core
```

Adding a cache source is one `ICleanupProvider` plus tests; the safety model then applies to it
uniformly. Providers hold knowledge and no orchestration; the planner holds orchestration and no
knowledge.

## Roadmap

File-table-backed full-drive scanning has landed: Explore reads the volume's MFT when the app runs
elevated, and walks whatever the table cannot account for. The Memory view has landed as well, with
the one action it will ever have: asking a program you pick to close itself, and nothing stronger.
Still to come: VS Code workspace storage with per-workspace
ages, Docker (reporting reclaim *inside* the VHDX separately from host space), and Android SDK.

Deliberately out of scope: `WinSxS`, `Windows\Installer`, and installer package caches. They are
large and tempting, but the failure modes are severe and the safe operations are already exposed by
`DISM` and the vendors' own tooling.

## Documentation

- [Specification](docs/todo/_spec.md) — the safety model, the audit evidence behind it, and the
  decided toolchain
- [CLAUDE.md](CLAUDE.md) — engineering gates for contributors and agents
- [CONTRIBUTING.md](CONTRIBUTING.md) — how to ask for a change, and why pull requests are not
  accepted

## Licence

[MIT](LICENSE).
