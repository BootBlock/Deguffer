<img src="assets/banner.svg" alt="" width="600">

# Deguffer

A Windows utility that finds and reclaims wasted disk space, with a safety model good enough to
trust. It knows what specific locations on your disk actually are, and tells you what each one
costs to lose, so you decide what goes.

**Guff** is British for nonsense, waffle, rubbish — the stuff that accumulates and serves no
purpose. **De-** removes it.

> **Status:** Milestone 1. Three cache sources, verified end to end. See [Roadmap](#roadmap).

## Why

Windows' own Disk Cleanup and Storage Sense understand Windows' caches, and stop there. Everything
else that quietly fills a drive — application and package caches, downloaded toolchains and
runtimes, per-workspace editor state, container images, search and index databases — is invisible
to them. Those are the bulk of the waste, and each one needs its own knowledge to clear safely.

Size alone cannot tell you which is which, so Deguffer does not rank folders by size and let you
guess. It recognises specific locations, says what each holds and what losing it costs, and leaves
the decision with you.

On the workstation this tool was designed against — Windows 11, ~330 GB system drive, down to
**5.6 GB free** — targeted cleanup of three package-manager caches recovered **22.9 GB** in a few
minutes without touching a single piece of user data.

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

| Source | Method | Tier |
| --- | --- | --- |
| NuGet | `dotnet nuget locals all --clear` | 1 |
| npm | `npm cache clean --force` | 1 |
| Gradle | Deletes `.gradle\caches` and `.gradle\wrapper` only | 1 |

Each reports "not installed" cleanly on a machine without that toolchain.

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
  Safety/      tier classification, disposable-child rules, long paths, machine seams
  Scanning/    size aggregation, free space
  Execution/   plan model, planner, executor, post-run verification
  Providers/   one class per known cache
Deguffer.Core.Tests/
Deguffer.App/  WinUI 3 shell, MVVM over Core
```

Adding a cache source is one `ICleanupProvider` plus tests; the safety model then applies to it
uniformly. Providers hold knowledge and no orchestration; the planner holds orchestration and no
knowledge.

## Roadmap

Milestone 2 and beyond: MFT/USN-based full-drive scanning (directory walking is far too slow to be
the scanner), then Tier 2/3 sources — VS Code workspace storage with per-workspace ages, Docker
(reporting reclaim *inside* the VHDX separately from host space), Android SDK, temp directories
with age filters and process exclusions.

Deliberately out of scope: `WinSxS`, `Windows\Installer`, and installer package caches. They are
large and tempting, but the failure modes are severe and the safe operations are already exposed by
`DISM` and the vendors' own tooling.

## Documentation

- [Specification](docs/todo/_spec.md) — the safety model, the audit evidence behind it, and the
  decided toolchain
- [CLAUDE.md](CLAUDE.md) — engineering gates for contributors and agents

## Licence

[MIT](LICENSE).
