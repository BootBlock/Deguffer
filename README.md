<img src="assets/banner.svg" alt="" width="600">

# Deguffer

A Windows utility that finds and reclaims wasted disk space, with a safety model good enough to
trust. It knows what specific locations on your disk actually are, and tells you what each one
costs to lose, so you decide what goes.

**Guff** is British for nonsense, waffle, rubbish — the stuff that accumulates and serves no
purpose. **De-** removes it.

> **Status:** Version 0.70.0. Thirty-five sources across the four tiers, plus a file-table-backed
> Explore view of the whole drive. See [Roadmap](#roadmap).

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

Previewing that same drive today, version 0.70.0 recognises 34 locations and finds 29 of them
present. It offers **10.3 GB** of Tier 1 cache pre-selected and **1.4 GB** of Tier 2 beside it, and
reports **6.4 GB** of Tier 3 user data separately with nothing pre-selected. The Recycle Bin and the
crash dumps are real space, and they are still yours to decide about.

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
- **Nothing is deleted without a preview.** Preview is the primary action; cleaning is a separate,
  explicit step.
- **Verify the negative.** After acting, assert that the things that should have survived did —
  config files, protected directories — and report it. This turns "I think it worked" into
  evidence, and catches an over-broad rule on the first run rather than the hundredth.
- **A locked file is the OS protecting live state.** Access-denied is skipped, not escalated.
- **Long paths are mandatory.** NuGet and Node trees routinely exceed `MAX_PATH`, and truncating
  there is the likeliest cause of a silent partial deletion.

## What it handles today

Thirty-five providers, each holding its own knowledge of one location. A provider reports "not
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
| .NET intermediate build output | `obj` directories under your own source trees |
| Dart analysis server cache | |
| VS Code editor caches | |
| VS Code C/C++ IntelliSense cache | |
| Chromium application caches | |
| Firefox caches | |
| Steam web cache | |
| Epic Games launcher web cache | |
| Epic Games launcher store artwork | Machine-wide, under `%PROGRAMDATA%`, and shared by every account |
| GPU shader caches | |
| Squirrel updater leftovers | Staging directories an interrupted update left behind |

**Tier 2 — regenerable, with cost.** Re-created by re-downloading or rebuilding.

| Source | Notes |
| --- | --- |
| Maven local repository | |
| Conda package cache | |
| vcpkg build caches | |
| PlatformIO cache and unused packages | PlatformIO's own prune decides which installed packages nothing still needs |
| Playwright browsers | |
| Azure Functions Core Tools releases | |
| Node.js project dependencies | `node_modules` under your own source trees |
| Python virtual environments | |
| Rust build output | `target` directories under your own source trees |
| Unity project library | |
| Superseded application versions | Older versions a Squirrel-updated app still keeps |

**Tier 3 — user data.** Never pre-selected, and shown with what losing it costs.

| Source | Notes |
| --- | --- |
| Recycle Bin | |
| Windows File History | Windows' own command drops saved versions past an age you set; the backup drive itself is never touched |
| Crash dumps and error reports | |
| Windows servicing logs | |
| VS Code editor logs and crash reports | |
| Epic Games launcher logs and crash reports | |

**Tier 4** is not a list of sources. It is everything a provider does not recognise, which is
excluded by construction rather than by enumeration.

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
elevated, and walks whatever the table cannot account for. Still to come: VS Code workspace storage
with per-workspace ages, Docker (reporting reclaim *inside* the VHDX separately from host space),
Android SDK, and temp directories with age filters and process exclusions.

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
