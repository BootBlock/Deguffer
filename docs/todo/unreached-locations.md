# Unreached locations — what the shipped providers do not see

> **Status:** 🟢 ACTIVE — a researched candidate set, sequenced and under way. §5's GPU shader
> caches and §7's per-volume recycle bins have shipped; everything else is unstarted. Flip to
> ✅ COMPLETE and `git mv` into `done/` when the list is exhausted, or supersede it with a newer
> plan.

[after-the-scanner.md](after-the-scanner.md) sequences the work that follows the §5.5 scanner, and
its provider items are drawn from the founding audit's own tables. That audit was a snapshot of one
machine on one day, so it is a description of where the space went, not a survey of where space
goes. This document is the survey: the locations Deguffer's shipped providers do not reach, researched
against vendor documentation and measured on one Windows 11 workstation.

[../cache-locations.md](../cache-locations.md) is the shipped, user-facing guide to what Deguffer
cleans today, and it carries the *rejected, with the reason* record for candidates already
considered. Nothing already recorded there is repeated here. `%USERPROFILE%\.cache`, the Dart/Flutter
pub cache, the Android SDK, `%LOCALAPPDATA%\Temp` and Docker are all settled subjects with their
reasoning written down; read that first.

Each entry below carries a proposed tier and says plainly whether it was **measured** or only
**researched**. That distinction is the point of the format. A measured entry was present on the
audited machine and sized there. A researched entry rests on vendor documentation, which is worth
less, and G8 says so.

---

## What the measurement found

Sizes observed on one Windows 11 workstation, on a machine the current nine providers had already
been run against. Every row is outside what those providers see. Paths are in environment-variable
form throughout this document.

| Location | Observed | Proposed tier | Why the current providers miss it |
| --- | ---: | --- | --- |
| `%LOCALAPPDATA%\Packages\<pkg>\LocalCache\Roaming` | 10.7 GB | mixed | MSIX redirects an app's `%APPDATA%` here. Nothing looks under `Packages` |
| Container data disk (`*.vhdx`) | 8.5 GB | 2 | Known (§5.4), still unbuilt. Needs the two-number report |
| Unity per-project `Library\` (7 projects) | 5.4 GB | 2 | Per-project build output; only `obj\` has a provider |
| Store-Python `LocalCache\local-packages` | 5.4 GB | 3 | Same redirection blind spot; it is installed packages, not cache |
| `$Recycle.Bin` on non-system volumes | 3.6 GB | 3 | Cleaners empty `C:` only. `C:` held 0 bytes here. **Shipped — see §7** |
| `%LOCALAPPDATA%\NVIDIA\DXCache` + `GLCache` | 3.2 GB | 1 | GPU shader cache; no provider category existed. **Shipped — see §5** |
| Windows Search index (`Windows.db`) | 2.2 GB | 2 | Needs a service stop, so no cleaner attempts it |
| `C:\$WinREAgent` | 1.7 GB | 2 | Disk Cleanup's update pass does not remove it |
| .NET SDKs, 8 versions, one out of support | 1.8 GB | 4 | An uninstall, not a delete. §2 rules it out |
| Visual Studio `.vs\` per solution (4 solutions) | 0.8 GB | 1 | Inside source trees, beside the `obj\` already walked |
| Chromium-shaped caches across 10 desktop apps | 0.8 GB | 1 | Recognisable by shape, not by name |
| Crash dumps, CBS logs, WER archives | 0.2 GB | 1 | Small here; routinely tens of GB after a bugcheck |

That is roughly 39 GB, of which about 25 GB is reclaimable once the Tier 3 and Tier 4 rows come off.

**The audited machine is a light case, and the gaps matter more than the totals.** It has no Adobe
install, no Rust or Go toolchain, no Unreal cache and an empty model store. Those are the entries
marked *researched* below, and on a machine that has them they are usually the largest numbers on
the list. Do not read the table as a ranking.

---

## 1. Package managers with an official eviction command

The closest fit to what Deguffer already does. Each has a §5.1 command or a §5.2 child set, so the
existing provider shape applies almost unchanged. Cleaners miss them because each needs its own
knowledge and there are a lot of them.

### Cargo — Tier 1, researched

Four disposable children under `%USERPROFILE%\.cargo`: `registry\cache` (downloaded `.crate`
archives), `registry\src` (their extracted contents), `git\db` and `git\checkouts`. Reported to
reach 50 GB on a working machine. Cargo has no stable prune command — garbage collection is still
unstable — so this is a §5.2 path-based provider with Gradle's shape.

**The §5.2 trap is live.** `config.toml` and `credentials.toml` sit in the same root, and the second
holds registry authentication tokens. `.cargo\bin` holds every binary installed with `cargo install`
and is normally on `PATH`. Recognised children only, and the unrecognised case is the one to test.

`%USERPROFILE%\.rustup\toolchains` holds a full toolchain per installed channel. Tier 2, and a
separate provider — not a child of this one.

### Go — Tier 1, researched

Two locations, two commands, and both must be located rather than assumed: `go env GOCACHE`
(default `%LOCALAPPDATA%\go-build`) cleared by `go clean -cache`, and `go env GOMODCACHE` cleared by
`go clean -modcache`.

**The module cache is deliberately read-only.** Go marks every extracted module file read-only so a
build cannot mutate a dependency in place. A path-based remover fails on it with access-denied per
file, which §5.3 says to treat as normal and skip — so a path-based Go provider would silently
reclaim nothing while reporting success. Use the command, or clear the attribute explicitly and
prove it with a test that fails without the clearing.

### pnpm — Tier 1, researched

`pnpm store path` locates it and `pnpm store prune` evicts only what no project on the machine still
references. A better eviction than npm's, because it is selective rather than total.

**Measuring it the ordinary way reports a number that is not true.** pnpm hard-links store contents
into every `node_modules` that uses them, so one set of blocks appears under many paths. Summing
file lengths counts each copy, and the disk gives back only the blocks whose last link went away.
This is §5.4's lesson in a different costume: report what will actually be freed, or the user prunes
4 GB and watches free space move by 400 MB.

The MFT reader can answer this where a directory walk cannot — the file record carries a hard-link
count, so a link-aware sum is available on the fast path. This is the first candidate that needs the
scanner to be more than fast, and it is the reason to build it early rather than late.

### Conda — Tier 2, researched

Anaconda's own documentation puts the `pkgs` directory at tens to hundreds of gigabytes.
`conda clean --all` removes tarballs, unused cached packages, the index cache and the source cache.
`conda clean --all --dry-run` reports the figure without acting, which is a preview Deguffer can
show directly rather than measure — the same relationship PlatformIO's `prune --dry-run` already
has.

Tier 2 rather than Tier 1: unpacked packages in `pkgs` are hard-linked into every environment that
uses them, so pnpm's accounting caution applies, and re-creating an environment is a download rather
than a rebuild.

### The long tail — Tier 1

One class each, on the shape npm and NuGet already use.

| Tool | Location and method | Note |
| --- | --- | --- |
| Maven | `%USERPROFILE%\.m2\repository`, no official purge | `.m2\settings.xml` in the root can hold encrypted server passwords. §5.2 verbatim |
| Yarn | `yarn cache dir`, then `yarn cache clean` | Yarn 1 and Berry differ; probe rather than assume |
| Bun | `%USERPROFILE%\.bun\install\cache` | No eviction command as of early 2026, so path-based by necessity |
| Composer | `composer clear-cache` | Measured at 30 MB, so the location is real rather than hypothetical |
| vcpkg | `%LOCALAPPDATA%\vcpkg\archives`, plus `buildtrees`, `downloads`, `packages` under the vcpkg root | The cache root moves with `VCPKG_DEFAULT_BINARY_CACHE`, the same problem `PLAYWRIGHT_BROWSERS_PATH` already forced into `IUserEnvironment` |
| Conan | `conan cache clean` | Build and download folders |
| Chocolatey | `%TEMP%\chocolatey` by default | Moves if `cacheLocation` is set in `chocolatey.config`; read the config |
| Scoop | `scoop cache rm *`, `scoop cleanup *` | The second removes superseded app versions under `apps\<name>\<version>` |

---

## 2. Per-project build output

The seam already exists. `DotNetObjProvider` walks source roots through `SourceRootStore`, and the
age column built for the cpptools workspace databases is exactly the signal these need. What is
missing is the other kinds of build directory.

### Unity `Library\` — Tier 2, measured at 5.4 GB across 7 projects

Unity regenerates `Library\` from the project's assets and settings, so nothing is lost. Tier 2
rather than Tier 1 because the regeneration is a full asset reimport, which on a large project is
tens of minutes. The largest single one measured 1.59 GB.

The recognition rule is strong: a directory named `Library` whose parent also holds `Assets`,
`Packages` and `ProjectSettings` is a Unity project. That is a content signature over the *parent*,
and `ContentSignature` already makes that kind of judgement for the cpptools workspace databases.

### Rust `target\` — Tier 1, researched

The largest per-project directory in common developer use, routinely 5 to 20 GB per workspace,
because every dependency is compiled per profile and per feature set. Recognised by a sibling
`Cargo.toml`, and evicted with `cargo clean` run in the project directory — the §5.1 path.

### Visual Studio `.vs\` — Tier 1, measured at 0.8 GB across 4 solutions

Per-solution IntelliSense database, browsing data and editor state, hidden beside the `.sln` and
rebuilt on the next solution open. The three largest measured 384 MB, 214 MB and 212 MB.

**One child is not cache.** `.vs\<solution>\v17\.suo` holds the user's own solution options: open
documents, breakpoints, expanded nodes, window layout. Small, and Tier 3. The recognised-children
rule matters here even though the directory is otherwise disposable.

### The rest of the shape — Tier 2, measured at 3.2 GB in the top five

Outside Unity, the audited machine held 1.24 GB in a `dist\`, 1.17 GB in a Dart `build\`, 803 MB in
a Python `.venv\`, and 681 MB across two `node_modules\`. Each is regenerable from a manifest beside
it, and each is worthless in a project nobody has opened for a year.

**The design question is not which names to recognise. It is that age is the whole decision**, and
§7 already calls age a first-class column. A dormant project's `node_modules` is free space; the
same directory in the project open in another window is a broken build. Per-step selection already
carries this without new UI.

**A live tree must never be a target.** §5.3's running-process exclusion is written for `%TEMP%`, and
this category needs it as much: a `.venv` with an activated interpreter, a `target\` mid-build, a
`Library\` with the editor open. All three fail badly and all three are invisible to a name check.
Generalising that exclusion is a prerequisite for this item, not a detail inside it.

---

## 3. MSIX redirection — a blind spot, not a location

This finding generalises furthest. It is one rule, and it makes an unbounded set of app caches
visible at once.

A packaged (MSIX) application does not write to `%APPDATA%\<App>`. Windows redirects it to
`%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\Roaming\<App>`, with `LocalCache\Local`,
`TempState` and `AC\INetCache` alongside. Every rule written against the unpackaged path silently
matches nothing, and the package family name gives no clue which vendor owns it.

Two packages on the audited machine account for 16.1 GB between them, and neither is
straightforwardly reclaimable. That is the point:

- **10.2 GB** was a single virtual-machine bundle inside one app's redirected data. §4.4's
  "application VM disk images" entry, unchanged in kind and larger in size. Tier 4.
- **5.4 GB** was `LocalCache\local-packages` under the Microsoft Store build of Python — the
  user-site directory where `pip install --user` puts packages. It is named `LocalCache` and it is
  not a cache. Tier 3 at best.
- Genuine Chromium cache under the same redirected root came to 33 MB.

**This is §3's mistake in a new costume.** The path segment is literally `LocalCache`. A size-ranked
list would put both entries near the top, and both are wrong to delete. The correct work here
classifies the redirection and then applies the same per-app rules it would have applied
unpackaged. It never treats `LocalCache` as a licence.

---

## 4. Chromium-shaped caches, recognised by shape

Cleaners handle browsers. Almost none handle the desktop applications that embed the same engine,
each carrying the same cache directories under its own vendor name.

Chromium writes a fixed set of directory names into whatever `userData` folder its host chose:
`Cache\Cache_Data` (HTTP cache), `Code Cache` (compiled V8), `GPUCache`, `DawnGraphiteCache` and
`DawnWebGPUCache` (pipeline blobs), and `Service Worker\CacheStorage`. All six are regenerated on
demand.

Scanning one level under `%APPDATA%` and `%LOCALAPPDATA%` for that signature found ten applications
over 20 MB on the audited machine — a package manager's own UI at 867 MB, a chat client at 250 MB,
an editor at 178 MB, and seven more. Individually unremarkable; as a category, near a gigabyte, and
the published figure for a single heavily used chat client is 2 to 5 GB.

**What sits beside them is Tier 3 and looks identical.** `Local Storage`, `Session Storage`,
`IndexedDB`, `Local State` and `Cookies` are in the same directory in the same naming style, and
hold sign-in tokens, drafts and offline application data. This is only safe as an exact allow-list
of the six names, with every unrecognised sibling kept at Tier 4 — §5.2 applied to a signature
instead of a root.

---

## 5. GPU shader caches — the purest Tier 1 on the disk ✅ done

**Outcome:** shipped as `GpuShaderCacheProvider`, one provider over four locations rather than one
per vendor. Every vendor's cache is the same fact — driver-version-keyed pipeline blobs, rebuilt on
demand — so the tier, the consequence and the reasoning are identical; what differs is only which
directory and which child names, and that is data. Each root still carries its own
`DisposableChildSet`, so §5.2 is answerable from one table, and per-vendor control survives because
selection is per step.

Four things the work settled that this section did not anticipate:

- **`accounts` is a file, and that is why each root declares protected names separately.** It was
  first written as a Tier 4 entry in the child set, which classified nothing: child classification
  enumerates *directories*, so a file in a tool root is never seen, never classified and never
  asserted. Driving the app is what found it — the plan showed the Intel notes and no NVIDIA one.
  Gradle already had the answer in `gradle.properties`, and the lesson generalises: for every root a
  provider enumerates, ask what non-directories are sitting in it.
- **`%LOCALAPPDATA%\D3DSCache` is the first whole-directory target**, because it has no tool root to
  enumerate: its parent is the profile, and its children are opaque per-application containers a
  name rule could recognise none of. §5.2's substance holds — the parent is never enumerated or
  targeted, and it is what §5.6 asserts survived.
- **A vendor root existing is not presence.** `%LOCALAPPDATA%\Intel` is present on machines with no
  Intel graphics cache at all, so `IsPresentAsync` probes the declared cache paths rather than the
  roots. This is the Unreal lesson from §8 of this document arriving in a second costume.
- **A target reached by name needs the reparse check that enumeration was giving away for free.**
  `D3DSCache` is the first target that does not come from a child enumeration, and the enumeration
  is where every other target had its junctions filtered out. Worse, `DirectoryRemover` guarded
  *entries* and never the root handed to it, so a junctioned target would have been enumerated
  through and the link's contents deleted — with the §5.6 negative passing, because every path it
  asserts lives inside the profile the deletion had already left. Both ends are now closed and both
  are tested. The general lesson is the one the review surfaced: **the safety property was riding on
  a filter nobody had named**, and it held only for as long as every target happened to arrive the
  same way.

Left out deliberately, and still open: AMD's other children beyond `DxCache`, which no available
machine could establish; Steam's `steamapps\shadercache`, which is per-game under a library root
rather than under `%LOCALAPPDATA%`; and the extracted driver installers below, which are leftover
installers rather than shader caches and want their own provider.

Compiled shader pipelines, keyed by driver version and discarded by the driver itself whenever that
version changes. Regenerated transparently. The only cost of deleting one is a few seconds of
stutter the first time a scene renders, and nothing can be lost.

- `%LOCALAPPDATA%\NVIDIA\DXCache` — 3.22 GB measured, the single largest Tier 1 candidate found.
- `%LOCALAPPDATA%\NVIDIA\GLCache` — 6 MB, same rule.
- `%LOCALAPPDATA%\D3DSCache` — the Direct3D system cache, 1 MB.
- AMD `%LOCALAPPDATA%\AMD\DxCache` and Intel `ShaderCache` — the same shape from the other vendors.
- Steam `steamapps\shadercache`, per game id — empty here, commonly several GB where pre-caching is
  enabled.

Adjacent, and worth the same provider's attention: `%PROGRAMDATA%\NVIDIA Corporation\Downloader` and
`C:\NVIDIA` keep whole extracted driver installers after installation. Tier 1, and nothing removes
them.

---

## 6. Windows leftovers that are not Windows servicing

§9 excludes `WinSxS`, `Windows\Installer` and the installer package caches, and that exclusion
should hold. Everything below is a log, a crash artefact or a completed upgrade's scaffolding — a
different kind of thing with a different failure mode.

### Crash dumps and error reports — Tier 1, measured at 0.15 GB

Five locations, all of them records of something that already happened: `%LOCALAPPDATA%\CrashDumps`
(84 MB), `%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive` and `ReportQueue`,
`C:\Windows\LiveKernelReports`, `C:\Windows\Minidump`, and `C:\Windows\MEMORY.DMP`.

All small here. They belong on the list anyway because `MEMORY.DMP` on a full-dump configuration is
the size of installed RAM — a single file of 32 or 64 GB after one bugcheck. Two 33 MB Electron
crash dumps also turned up inside a container tool's log directory, which suggests dumps are worth
finding by shape as well as by location.

### Completed upgrade scaffolding — Tier 2, measured at 1.74 GB

`C:\$WinREAgent` is the recovery-environment servicing folder, and 1.74 GB of it was still present.
Disk Cleanup's *Windows Update Cleanup* pass does not remove it. Its siblings are `$GetCurrent`,
`$SysReset`, `$Windows.~BT`, `$Windows.~WS` and `C:\ESD`.

**This one is conditional, and the condition is checkable.** Removal is documented as safe only when
the update that created it has finished and no restart is pending. A provider must test that, not
assume it — getting it wrong interrupts a servicing operation mid-flight. **If that check cannot be
made reliable, this entry belongs in §9 beside `WinSxS` rather than in the product.** Establish the
check before writing the provider.

### Servicing and update logs — Tier 1, measured at 64 MB

`C:\Windows\Logs\CBS` held 64 MB and is regularly reported in the gigabytes on a machine with a long
update history. `C:\Windows\Logs\WindowsUpdate`, `C:\Windows\Panther` (setup logs from every
in-place upgrade) and `C:\Windows\System32\LogFiles\WMI\RtBackup` are the same kind of thing.

### Delivery Optimization and the search index — Tier 2, measured at 2.18 GB

The Delivery Optimization cache under
`C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization`
holds update payloads kept for peer distribution. Regenerated as needed, and clearing it does not
break Windows Update.

The search index is the more interesting one. **On Windows 11 it is `Windows.db`, not the
`Windows.edb` every guide still names** — 1.94 GB measured, with a further 237 MB in
`Windows-gather.db` and `Windows-usn.db` beside it. Fully rebuildable, so Tier 2, but the cost is
real: search does not work until reindexing finishes, which is hours.

**It cannot be deleted from a running system.** The Windows Search service holds the file open, so
the operation is stop-service, delete, start-service, under elevation. That is a different kind of
step from anything `CleanupPlan` models today. **Decide whether Deguffer takes on service control at
all before this is scheduled.**

---

## 7. Things every cleaner does on `C:` and nowhere else

### Per-volume recycle bins — Tier 3, measured at 3.6 GB ✅ done

**Outcome:** shipped as `RecycleBinProvider`, and as the first Tier 3 provider in the product. Each
step targets one fixed volume's directory for the *current account's* security identifier, never
the `$Recycle.Bin` root that contains it — the root is a shared parent in §5.2's exact sense, since
another person's deleted files and `S-1-5-18`'s sit beside this user's, told apart by nothing but a
string of digits. Windows re-creates the per-account directory on the next delete, so the bin keeps
working. Per item was the other candidate and is the wrong grain twice over: the paired `$I` and
`$R` entries must go together, and §7's age column wants a row a reader can act on rather than ten
thousand of them.

**This section's claim about volume enumeration was wrong, and the work had to build the seam.**
Nothing in the solution enumerated volumes: `DirectoryScanner` derives a drive letter from the path
it is handed via `VolumePath.TryParse`, `IMftSourceFactory.TryOpen` is given the letter to open, and
no call to `DriveInfo.GetDrives` existed anywhere. The MFT scanner never needed the machine's volume
list because it is only ever asked about a path somebody already chose. So this cost a new seam,
`IVolumeInventory`, rather than being nearly free.

Three further things the work settled that this section did not anticipate:

- **The seam is its own interface rather than a member on `IUserEnvironment`.** That interface is
  the signed-in user — their profile directories, their `PATH`, their environment — and the mounted
  volumes are a fact about the hardware. Describing one type as "the user and the disks" is G1's own
  test for two types. What *did* go onto `IUserEnvironment` is the account's security identifier,
  which is the user, and which is null when it cannot be established — because a provider that
  cannot say who it is running as must recognise no bin at all.
- **The seam reports each volume's kind rather than filtering by it.** Which kinds may be acted on
  is a safety decision belonging to the provider, and a seam that filtered would leave that decision
  untestable, since no fake could present the kind being refused. The provider takes fixed, ready
  volumes only: a network share has no bin at all, so a `$RECYCLE.BIN` on one belongs to the
  server's users, and removable media can be swapped between the preview and the clean.
- **The first Tier 3 subject made an existing sentence read as a contradiction.** `Tier 3 requires
  typed confirmation` was already built, and its wording told the user their data "is not sent to
  the Recycle Bin" — which against a plan whose whole business is emptying them is not a sentence
  anybody can act on. The generic requirement now says the loss is total and leaves the mechanism to
  each plan's own `WhatHappensOnNextUse`.

Tier 3 without argument: the contents are files a user deleted and can still restore, which is the
definition of recoverable user data. Per-volume rows with an age fit the existing selection model.

### Shadow copies and restore points — Tier 3, unmeasurable unelevated

Volume Shadow Copy storage is allocated per volume up to a configured maximum, commonly a
substantial share of the volume. `vssadmin list shadowstorage` reports allocated, used and maximum,
and **it refused to run unelevated during this research** — which is itself the finding, because the
size is then unknowable on the app's default unelevated run.

The honest treatment is probably to report it and not offer it. Shrinking the maximum is the safe
lever, deleting shadow copies destroys restore points and any previous-versions history, and both
are already exposed by the vendor's own tooling. **This likely belongs in §9 beside `WinSxS`.**

---

## 8. Creative and media caches

### Adobe media cache — Tier 1, researched

`%APPDATA%\Adobe\Common\Media Cache Files` and `Media Cache` hold conformed audio and indexed video
for every clip ever opened in Premiere or After Effects. Adobe's own documentation describes manual
deletion as supported, and a commonly cited figure is around 50 GB. Regenerated by re-conforming the
source media, so nothing is lost, and Adobe ships its own age-based and size-based eviction in
preferences — a signal that path deletion here is expected rather than clever.

The same shape appears in `%LOCALAPPDATA%\Adobe\CameraRaw\Cache`, in DaVinci Resolve's cache and
optimised-media directories, and in Blender's temporary render directory. None appeared on the
audited machine, so all of it is documentation rather than observation.

### Game engine derived-data caches — Tier 2, present but empty

Unreal's local derived-data cache at `%LOCALAPPDATA%\UnrealEngine\Common\DerivedDataCache` is
reported by Epic's own forums at 16 GB to 100 GB, growing 1.5 to 2 GB per day of active work. Unreal
5.4 and later moved the default to a Zen store under `%LOCALAPPDATA%\UnrealEngine\Common\Zen\Data`
and put the old filesystem cache into delete-only mode with an eight-day expiry, so a provider has
to handle both layouts — and the legacy one is stale by definition.

The directory exists on the audited machine at zero bytes, which is the useful negative result:
**presence of the directory is not evidence of a working install**, so `IsPresentAsync` must test for
content, not existence.

---

## 9. Model weights, where "regenerable" stops being true

`%USERPROFILE%\.ollama\models`, LM Studio's model directory and the Hugging Face hub cache are each
tens of gigabytes on a machine that uses them, and every one of them looks like a download cache.

They are not, for the reason that already got `%USERPROFILE%\.cache` rejected in
[../cache-locations.md](../cache-locations.md): a model that was gated, withdrawn, or replaced by a
newer revision under the same name cannot be re-downloaded. "Regenerable" is a claim about the
future availability of a remote artefact, and for model weights that claim is frequently false.
**Tier 3 is the honest classification**, and the size makes the failure expensive.

`ollama list` and `ollama rm` give a per-model view with real names, which is a far better subject
than a directory of hashes. If this is ever built, build it on that.

---

## 10. The category that needs a different verb

OneDrive, Google Drive and Dropbox all keep local copies of cloud files, and all three expose an
operation that frees the local blocks while leaving the file present, browsable and identical in the
cloud. On OneDrive it is Files On-Demand's *Free up space*, which sets the file to a placeholder
state. Nothing is deleted and there is nothing to lose.

The audited machine held 178 MB of local cloud cache, which is small. It earns its place because it
is one of very few operations on this whole list with a genuinely zero-consequence outcome, and on
a machine syncing a large shared drive it is tens of gigabytes.

**It does not fit `CleanupPlan`.** Every step Deguffer models today is a deletion or a tool's
eviction command. This is neither: the file survives, and the correct §5.6 negative asserts that the
file **still exists and is still readable**. Building it means a third kind of step. That is a real
design decision, and it should be taken deliberately rather than discovered while writing a
provider.

---

## 11. Virtual disks — §5.4, and why the picture got worse

§5.4 already states the rule: freeing space inside a virtual disk does not free it on the host, so
report the two numbers separately. The audited machine carried an 8.49 GB container data disk and a
104 MB WSL distribution disk, both under a packaged app's local data — so §3's MSIX finding and
§5.4 land on the same file.

What has changed since §5.4 was written is that the compaction half is now harder, not easier:

- WSL gained sparse VHD support, which reclaims space automatically. Sparse disks then **break
  `Optimize-VHD`**, which refuses any file that is sparse, compressed or encrypted. The Windows-side
  compaction path a provider would reach for fails on exactly the disks that are newest.
- For a sparse disk the reclaim is `fstrim` run inside the Linux guest, which is not an operation a
  Windows cleanup tool can perform.
- Sparse VHD was placed behind `--allow-unsafe` in WSL 2.5.6 after reports of VHDX corruption.

**The safe scope is reporting, not acting.** Measure the host file, ask the container tool for its
internal reclaimable figure, show both, and name the vendor command. Anything past that is
compaction, and compaction on a live or sparse virtual disk risks the whole image. Do not run it.

---

## 12. Superseded toolchain versions — mostly out of scope

The audited machine has eight .NET SDKs installed side by side, one out of support, totalling
1.78 GB. The same pattern produces old JetBrains IDE directories — JetBrains publishes a support
article about it, and reports of 32 GB in one user's `%LOCALAPPDATA%\JetBrains` — plus superseded
Node versions under a version manager, old application builds, and stale Visual Studio component
caches.

**Most of this is out of scope and should stay out.** §2 says Deguffer is not an uninstaller, and
removing an SDK by deleting its directory leaves the installer's own records claiming it is present.
The right tools exist and are the vendors': `dotnet-core-uninstall`, JetBrains' *Delete Leftover IDE
Directories*, and the version manager's own remove command.

Two carve-outs are defensible, because both are caches rather than installations:

- JetBrains' `caches`, `index` and `log` subdirectories under each per-version folder. The IDE
  itself deletes these after 180 days, so an eviction policy already exists to match.
- Visual Studio's `ComponentModelCache`, a MEF index rebuilt on next launch.

Both are §5.2 recognised children under a root that must never be targeted whole, because `settings`
and `plugins` live in the same place.

---

## The negative list

Knowing what to refuse is the same product as knowing what to offer, and several of these are things
competing cleaners actively do. Worth encoding as knowledge, not merely omitting.

| Location | Why not |
| --- | --- |
| `C:\Windows\Prefetch` | Small, and clearing it makes application launches slower until Windows rebuilds it. A cleaner that empties it has made the machine worse |
| `$UsnJrnl`, `$LogFile` | Can be large, and some tools offer to delete the USN journal. Deguffer's own scanner reads volume metadata, so this is self-harm as well as user harm |
| Font, icon and thumbnail caches | Tens of megabytes. Not worth a row, and rebuilding them causes visible flicker across the shell |
| `pagefile.sys`, `hiberfil.sys`, `swapfile.sys` | System-managed. §4.4 already excludes these |
| Chromium `Local Storage`, `IndexedDB`, `Cookies` | Sit beside the six safe cache names and hold sign-in state and offline data. Tier 3 |
| `.cargo\credentials.toml`, `.m2\settings.xml` | Registry authentication tokens and encrypted server passwords, in the root of a directory whose children are being deleted. The §5.2 case exactly |
| Steam `steamapps\downloading` | Looks temporary. Holds the in-progress half of a patch; deleting it restarts the download |
| `.vs\...\.suo` | The user's own solution options, inside an otherwise disposable directory |

---

## Sequencing, and what it is based on

Not a schedule. An observation about what each item costs, given the machinery that already exists.

| Candidate | What it needs | Observed |
| --- | --- | ---: |
| GPU shader caches ✅ | Nothing new. Path-based, recognised children, pure Tier 1 | 3.2 GB |
| Per-volume recycle bins ✅ | A volume-enumeration seam, which had to be built — the scanner never had one. Tier 3 confirmation already existed | 3.6 GB |
| Chromium cache signature | A signature match over app-data folders; `ContentSignature` has the shape | 0.8 GB |
| Crash dumps and servicing logs | Path-based, plus elevation for the `C:\Windows` paths, which §6.3 already permits | 0.2 GB |
| Cargo, Go, pnpm, conda, Maven, vcpkg | One class each, on the npm and NuGet shape. pnpm needs link-aware measurement | researched |
| Per-project build output | Extends `SourceRootStore`; needs §5.3's live-tree exclusion generalised beyond `%TEMP%` | 8.6 GB |
| MSIX redirection | A classification rule, not a provider. Changes what every other provider can see | 16.1 GB |
| Cloud sync dehydration | A third kind of `CleanupStep`, and a §5.6 negative that asserts survival rather than removal | 0.2 GB |
| Windows Search index | Service control, which Deguffer does not do today. Decide the policy before the provider | 2.2 GB |

The last two rows are different in kind from the rest. Cloud-sync dehydration and service control
are not new providers, they are new **capabilities**, and each widens what the safety model has to
reason about. The tier model handles them. The plan and execution types do not, yet.

---

## Open questions this survey raises

1. **Does `LongPath` cover a hard-linked file's identity?** pnpm and conda both need a link-aware
   size, and the MFT record carries the count. Whether the fallback walk can answer at all is
   unresolved, and a provider whose number is right only under elevation is a poor citizen.
2. **How is a live source tree detected?** §5.3's exclusion is written against `%TEMP%` and open
   handles. A `.venv` whose interpreter is running, or a Unity project open in the editor, needs the
   same answer at a different granularity.
3. **Is the pending-reboot check reliable enough to ship?** It gates `C:\$WinREAgent` entirely.
4. **Should Deguffer control services at all?** It gates the search index, and possibly nothing else.
   If nothing else, the answer is probably no, and the index belongs in §9.
5. **What does a non-deleting step look like in `CleanupPlan`?** Cloud-sync dehydration is the only
   known subject today, so building the abstraction now would be speculative generality under G3.
   The question is whether a second subject exists.
