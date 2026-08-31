# Unreached locations — what the shipped providers do not see

> **Status:** 🟢 ACTIVE — a researched candidate set, sequenced and under way. §1's Cargo, Go, Maven
> and vcpkg providers, §4's Chromium application caches, §5's GPU shader caches, §6's crash dumps
> and servicing logs, and §7's per-volume recycle bins have shipped; §1's pnpm and conda and
> everything else are unstarted. Flip to ✅ COMPLETE and `git mv` into `done/` when the list is
> exhausted, or supersede it with a newer plan.

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
| Chromium-shaped caches across 10 desktop apps | 0.8 GB | 1 | Recognisable by shape, not by name. **Shipped — see §4** |
| Crash dumps, CBS logs, WER archives | 0.2 GB | 3 | Small here; routinely tens of GB after a bugcheck. **Shipped — see §6**, and the tier is a correction |

That is roughly 39 GB, of which about 25 GB is reclaimable once the Tier 3 and Tier 4 rows come off.

**The audited machine is a light case, and the gaps matter more than the totals.** It has no Adobe
install, no Rust or Go toolchain, no Unreal cache and an empty model store. Those are the entries
marked *researched* below, and on a machine that has them they are usually the largest numbers on
the list. Do not read the table as a ranking.

---

## 1. Package managers with an official eviction command — Cargo, Go, Maven and vcpkg ✅ done

The closest fit to what Deguffer already does. Each has a §5.1 command or a §5.2 child set, so the
existing provider shape applies almost unchanged. Cleaners miss them because each needs its own
knowledge and there are a lot of them.

**Outcome:** four of the six shipped — `CargoCacheProvider`, `GoCacheProvider`,
`MavenRepositoryProvider` and `VcpkgCacheProvider`, with `VcpkgDiscovery` split out beside the last
of them. pnpm and conda did not, and the row in the sequencing table was split rather than ticked:
both are blocked on the link-aware measurement open question 1 records, and neither is a provider
until that is answered. Every one of the four is **researched rather than measured** — none of these
toolchains is installed on the machine this was written against, so the sizes here remain vendor
documentation and community report, and the rules are proved through the fakes rather than against a
real cache.

Nine things the work settled that this section did not anticipate.

- **The tier held for two and moved for two, and one argument moved both.** "Regenerable" is a claim
  about somebody else re-serving an artefact, and §9 of this document already makes that argument for
  model weights. It bites twice here. A Maven local repository is filled from two places: most of it
  came from a remote, but `mvn install` writes into the same tree in the same layout, and what it
  writes exists on no remote at all — so losing it does not make the next build slower, it makes it
  fail until somebody rebuilds the producing project. That is Playwright's shape exactly, and Tier 2
  is where Playwright already sits. vcpkg is Tier 2 for the other half of Tier 2's own definition:
  restoring a binary-cache entry is a *compile* rather than a download, and for a large library that
  is hours.

- **Cargo stayed Tier 1 only because two of the four children came off the list, and the split is
  the finding.** `git\db` is the bare clone of a git dependency and the only copy of that history on
  the machine; `git\checkouts` is re-created from it with no network at all. `registry\src` is
  unpacked from `registry\cache` the same way. Cargo's own documentation draws precisely that line
  when it says which parts of the home are worth carrying between CI runs — the originals are, the
  derived directories are not. So the provider takes `registry\cache`, `registry\src` and
  `git\checkouts`, and declares `git\db` and `registry\index` at Tier 4 with reasons that say why
  they stay. Where a location *can* be split into the safe half and the unsafe half along a
  directory boundary, splitting it is better than arguing the tier over the whole.

- **§5.1's answer is "no" three times out of four, and each "no" is different.** Cargo's garbage
  collector is still unstable and nightly-only; `cargo clean` is a per-project command for a
  different subject. Maven ships no machine-wide purge at all —
  `dependency:purge-local-repository` removes one project's dependencies and immediately re-resolves
  them. vcpkg's own answer is the `--clean-after-build` family of flags, which clean as a build goes
  rather than afterwards, plus documentation saying outright that `buildtrees`, `downloads` and
  `packages` are safe to delete. Each position is written into the class, so a reader can tell
  "considered and absent" from "never asked".

- **Go's commands close a trap rather than merely being preferred.** The module cache is read-only by
  design, so a path-based provider would meet an access-denied refusal per file, skip each one under
  §5.3, and reclaim nothing while reporting success. `go clean -modcache` is what knows how to take
  it apart. One `go env GOCACHE GOMODCACHE GOPATH` answers all three locations, and the third is
  what tells the provider which neighbours §5.6 has to assert survived.

- **The read-only trap was real, and it was in the remover rather than in any provider.** Windows
  refuses to remove a directory carrying `FILE_ATTRIBUTE_READONLY` exactly as it refuses a read-only
  file — observed directly here. `DirectoryRemover.TryDeleteFile` had always cleared the bit and
  retried; `TryDeleteDirectory` never did, so every file in such a directory went, the directory
  stayed, and the step reported success because bytes had been reclaimed. Fixed at the seam, with a
  test that failed first. Two things the fix had to get right: the exception does not discriminate,
  because .NET reports the same read-only directory as `UnauthorizedAccessException` for a plain path
  and as a bare `IOException` for the extended-length form §6.3 requires; and the retry is gated on
  the directory being empty, so a directory still holding a locked file keeps its attributes rather
  than having them reset on a path the removal is deliberately leaving standing.

- **The nested-child shape got a name rather than a second copy.** §4 answered Chromium's `Cache\Cache_Data`
  with a level per containing directory, and Cargo's `registry\cache` needed the same answer.
  `ChromiumCacheLevel` is now `CacheLevel`, in its own file, used by both — each provider still
  writes its own levels, so §5.2 stays answerable by reading one table. No fourth shape was added.

- **Depth was not the question that chose the shape.** Two of the four name their targets outright
  through `DeclaredRoot` and never enumerate: everything in `.m2` besides the repository is
  configuration knowable by name, and listing a vcpkg clone would produce half a dozen "not
  recognised" notes about `ports`, `triplets` and `scripts` while `installed` is far better served by
  being a named survivor. Cargo enumerates all three of its levels, because `.cargo` is an ordinary
  profile directory where listing costs nothing and a child a later Cargo adds should be reported
  rather than invisible. The deciding question is whether the parent's *other* children have to be
  classified and reported, exactly as expected — but the answer went the declared way twice, which
  the section did not.

- **A declared location's age is not always meaningful, and the seam had to say so.**
  `DeclaredLocations` reads an age from a location's immediate children, which is right for a dump
  folder or a directory of archives and wrong for a Maven repository: that nests by group, artifact
  and version, so its top level moves only when a whole new group first appears, and a repository
  built against daily would report as years old. `DeclaredLocation.ReportsAge` now carries that, and
  §7's column is then blank rather than carrying a date nobody should act on.

- **vcpkg is the first tool whose main directory is a clone the user placed, and the provider has to
  say what it could not see.** There is no profile location to fall back on: the clone is knowable
  only from `VCPKG_ROOT`, from the `vcpkg.path.txt` that `vcpkg integrate install` writes, or from the
  directory holding `vcpkg` on `PATH`. Where none answers, only the binary cache is covered — a
  quarter of the subject — and the plan carries a sentence saying so and naming what would fix it. A
  provider that silently reported a fraction of a cache would be worse than one that names the part
  it did not reach. Two smaller relocation findings came with it: the binary cache has a documented
  three-step search order rather than one variable, and `VCPKG_DOWNLOADS` set to the place vcpkg
  would have used anyway must not declare the same directory twice.

- **A configured root is a claim, and four separate holes came from treating it as a fact.** Review
  found every one of them, and they are one shape: a string from an environment variable or a
  settings file, used before anything established what it was. A junctioned Cargo home was declined
  at the root level and reached through anyway, because the next level resolved its own path through
  the link that had just been declined. A Maven `localRepository` naming `.m2` itself made the
  folder holding the credentials the target of the plan that promised they would survive. A stray
  `vcpkg.exe` would have declared `downloads`, `packages` and `buildtrees` under whatever directory
  held it. And a trailing separator on any configured path made the declared leaf name empty, which
  resolves back to the root — so the plan targeted the directory it also asserted must survive, and
  a `..` segment defeated `LongPath.Extended`'s stated requirement of an already-resolved path.
  `LongPath.Configured` now normalises such a value once, where it is accepted; each provider
  refuses one that would swallow the tool's own directory; and vcpkg requires the `.vcpkg-root`
  marker before it looks inside anything, which is the Chromium identification check in a second
  costume. **The same hole was already shipped in `PlaywrightBrowsersProvider`**, whose root is also
  configured and which also enumerated without classifying it, so that is closed here too, and
  Gradle's fixed root with it.

One defect surfaced that had nothing to do with any of these providers. `XDocument.Load` given a
path treats it as a URI, and §6.3's extended-length prefix is not one, so reading Maven's
`settings.xml` threw before it read a byte. It is opened as a stream now. Anything else in the
codebase that hands an extended path to an API expecting a URI has the same defect, and nothing
currently does.

Left out deliberately, and each for a reason: pnpm and conda, which are the split row below;
`%USERPROFILE%\.rustup\toolchains`, which is Tier 2 and its own provider rather than a child of
Cargo's; `.m2\wrapper`, which is disposable but holds Maven distributions of a few megabytes and is
named as a survivor instead; `VCPKG_BINARY_SOURCES`, which is a small expression language rather
than a path and whose remote-cache case leaves nothing local to find; and the rest of the long tail
below.

### Cargo — Tier 1, researched ✅ done

Four disposable children under `%USERPROFILE%\.cargo`: `registry\cache` (downloaded `.crate`
archives), `registry\src` (their extracted contents), `git\db` and `git\checkouts`. Reported to
reach 50 GB on a working machine. Cargo has no stable prune command — garbage collection is still
unstable — so this is a §5.2 path-based provider with Gradle's shape.

**The §5.2 trap is live.** `config.toml` and `credentials.toml` sit in the same root, and the second
holds registry authentication tokens. `.cargo\bin` holds every binary installed with `cargo install`
and is normally on `PATH`. Recognised children only, and the unrecognised case is the one to test.

`%USERPROFILE%\.rustup\toolchains` holds a full toolchain per installed channel. Tier 2, and a
separate provider — not a child of this one.

### Go — Tier 1, researched ✅ done

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

Maven and vcpkg shipped, at **Tier 2 rather than the Tier 1 this table proposed** — see the outcome
above. The rest are unstarted.

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

## 4. Chromium-shaped caches, recognised by shape ✅ done

**Outcome:** shipped as `ChromiumCacheProvider`, with the discovery walk split out into
`ChromiumUserDataDiscovery`. The split is the design: one type answers "whose folder is this?" and
the other answers "what inside it may go", and keeping them apart is what stops the second question
from ever being asked of a folder that failed the first.

Six things the work settled that this section did not anticipate:

- **A cache name is not identification, and the two judgements had to be separated to say so.**
  This section framed the signature as the whole rule, but the six names only say what may be
  deleted — they never establish whose folder it is, and any directory anywhere may be called
  `GPUCache`. A folder is now looked inside only once Chromium's own `Local State` marker is found
  in it. An application that has somehow never written that file is invisible here, and reclaiming
  nothing is the safe direction to be wrong in.
- **The two nested names needed a level per containing directory, not a wider child set.**
  A `DisposableChildSet` classifies a flat name against one parent, so `Cache\Cache_Data` cannot be
  one of its entries. Teaching it relative paths would change the question every provider's §5.2
  declaration answers from "which children may this tool delete?" into "which paths, at what depth,
  may it reach?" — strictly harder to check by reading, and being checkable by reading is the whole
  point. So `Cache` and `Service Worker` became levels of their own, and the rule stayed an
  exact-name allow-list over one directory's immediate children.
- **A container had to be *declared* Tier 4 rather than left unrecognised.** `Cache` and
  `Service Worker` are the one case where the unrecognised-child reason would have been actively
  false: the directory really is left standing, and something inside it really is being removed.
  The generic "we did not recognise that" wording is right for a sibling and wrong for a parent, so
  each carries its own reason, and the per-application note gains a sentence saying it — but only on
  a plan that actually emptied one. A user who sees `Cache` still there afterwards would otherwise
  have no way to tell that anything inside it went.
  A related correction the work forced: `Cache` holds nothing but `Cache_Data`. The index lives
  *inside* `Cache_Data`, not beside it, so the first reason written for that entry described a
  layout Chromium does not produce. It is kept because §5.2 never targets a directory whose
  children have not been classified, which is the honest reason and the one now written down.
- **One note per spared child does not survive contact with this folder.** Every provider before
  this one names each thing it left alone, which is right for a vendor directory holding two
  children and unusable for a Chromium profile holding fifty, across ten applications. A note
  nobody reads protects nothing, so the plan carries one sentence per application and §5.6 still
  asserts every spared directory individually.
- **The single-profile layout makes the folder its own profile.** An application embedding the
  engine writes the caches straight into its user-data folder, so that one directory is named both
  as the folder and as the profile and would have been verified twice, reporting one survivor as
  two. Protected paths are deduplicated for that reason and no other.
- **§5.3's warning has no names to declare.** The applications are discovered rather than known, so
  the folder's name stands in for the process's — which is right far more often than not for an
  application that named its own data folder. It decides nothing: a miss costs one absent warning,
  and a hit names a process the user can see and close.

Confirmed and left alone: MSIX redirection. `%LOCALAPPDATA%\Packages` needs no special case here,
because it holds no `Local State` of its own and the identification check skips it exactly as it
skips every other directory. Reaching the Chromium caches inside a packaged app is §3's work.
Scanning one level under the two application-data roots also leaves the browsers themselves out,
which is intended: Chrome and Edge keep their user data three levels down, and every
general-purpose cleaner already reaches them.

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

### Crash dumps and error reports — Tier 3, measured at 0.15 GB ✅ done

**Outcome:** shipped as `CrashDumpProvider`, at **Tier 3 rather than the Tier 1 this section
proposed**. That correction is the first of the things the work settled, and the rest follow from
reaching into `C:\Windows` at all.

- **The tier was wrong here, and the argument that fixes it is the one this section already made
  without noticing.** "Records of something that already happened" is not a description of a cache;
  it is §3's Tier 3 in so many words, and §3's Tier 3 row lists logs explicitly. Tier 1 requires that
  whatever produced the content re-creates it, so that nothing is lost — and nothing re-creates a
  crash dump, because the crash does not happen again to order. What Windows re-creates is the *next*
  dump. The reading is uncomfortable because these folders really are disposable most of the time,
  and every general-purpose cleaner treats them so; but "usually disposable" and "regenerable" are
  different claims, and telling them apart is what the tier model is for.

- **§5.2 could not be applied here in the form every other provider uses, and the replacement is
  stricter.** A `DisposableChildSet` answers "of the children I just enumerated, which may I
  delete?", which presupposes enumerating the parent — and listing `C:\Windows` is already the first
  step towards classifying something in it by a rule. So this provider names absolute paths and never
  enumerates anything: `DeclaredRoot` holds a root plus exact relative paths, and the unrecognised
  case cannot arise, because there is no enumeration through which an unnamed sibling could be
  reached. A consequence worth stating: a declared location carries **no tier of its own**, since
  there is no classification to disagree with the provider's, so the test every child-set provider
  owes itself has no subject here.

- **§9's exclusions had to become assertions rather than omissions.** Not naming `WinSxS` and
  `Windows\Installer` as targets proves nothing, because §5.6's whole point is that an over-broad
  rule passes every positive assertion. They are now named survivors on the declaration itself —
  `WindowsSystemRoot.Holding` carries them, so any future provider reaching into that directory gets
  them by construction rather than by remembering. Both provider test classes execute a plan and
  assert `WinSxS`, `Windows\Installer` and an unnamed neighbour still standing afterwards; the
  installer package cache sits under `%PROGRAMDATA%` and so is asserted by the crash-dump class
  alone, which is the only one declaring that root.

- **`MEMORY.DMP` needed a second kind of deletion, and the shape §10 inherits is the split rather
  than the step.** `DeleteFileStep` is trivial; what mattered was where it sits. Both deletions now
  derive from a `DeleteStep` base, and `CleanupPlan.TargetedPaths` and `NarrowedTo` select on *that*
  — so "everything this plan would destroy" stays one question with one answer, and a third deletion
  kind joins the §5.2 assertions and the §5.6 negative without an edit. §10's dehydration is
  precisely the case that must **not** be a `DeleteStep`: it frees space while leaving the file
  present, so it belongs beside `DeleteStep` and `RunCommandStep`, contributing no targeted path and
  a §5.6 negative that asserts survival. That distinction is what this work put in place; the step
  itself is four lines.

- **"I can see this and cannot remove it" was a claim the plan had no way to make, and it is not the
  one `FallbackReason` carries.** `NotElevated` describes a measurement that took the slow route.
  This is a different operation with a different answer, and the two are independent — a size read
  straight off the file table can still belong to a step nobody unelevated may perform. A step now
  carries `RequiresElevation` as a **declaration about the location**, not about the run, so it stays
  true on an elevated process; `ElevationOffer` reads both claims; and the shell pairs the
  declaration with the token it is actually running under. Deriving elevation from the path was
  rejected for the same reason §5.2 refuses every other guess: declared, it is checkable by reading.
  Driving the app showed the intended shape — five of the six rows marked, unticked and explained,
  the profile's own folder still selectable, and "Elevate and rescan" on screen.

- **The machine-wide directories are a third seam, on `IVolumeInventory`'s own test.**
  `%PROGRAMDATA%` and `C:\Windows` belong to the operating system and are shared by every account, so
  they are not the signed-in user and not the mounted volumes. `ISystemDirectories` exists so §5.2
  against `C:\Windows` can be proved on a machine where nobody may delete anything in it — which is
  the only way that proof can ever be run. It carries no `Invalidate`, unlike both older seams:
  neither directory can move while a process is running, so the method would exist to be symmetrical
  and to be forgotten.

- **§5.3 turned out not to want an age filter, and the reason generalises.** `%TEMP%` needs one
  because live working files sit among dead ones and look identical. Nothing in a dump folder is live
  except a dump being written, and that one is held open and skipped — so the lock *is* the
  live-state guard, and a cut-off would only be a guess about evidence value. What the hazard really
  wants is for the user to see it: Tier 3 leaves the row unticked, and each row carries the newest
  write inside it, which for `MEMORY.DMP` is the moment the machine stopped. A filter would also have
  had to change the grain from one directory to one dump, which §7's age column is not asking for and
  which `RecycleBinProvider` already rejected on its own subject.

Two defects surfaced that had nothing to do with this section, and both were seams rather than
providers. The fallback scanner answered zero for any path that was not a directory, so the largest
single reclaim in the product would have produced a step nobody could select — a single file is now
measured directly and reported as `ScanStrategy.DirectRead`, because a `stat` is not a slow scan and
should not carry a sentence apologising for one. And a plan whose every target turned out to be a
link collapsed to "nothing found", dropping the note that explained the refusal, which is the
"quietly disagrees with the folder" failure `RecycleBinProvider` guards against and this one did not.

Four residuals the work leaves behind, each documented rather than fixed and each for a stated
reason. They are recorded here because the next person reaching into a system directory meets all
four, and none of them is visible from the code alone.

- **The reparse predicate reports "I could not tell" as "it is a link".** `LongPath.IsReparsePoint`
  answers true for three outcomes — it is a link, we were refused, or the path could not be read —
  and fails closed deliberately, which is right for a predicate guarding a deletion. What is new is
  that this is the first caller to turn the answer into *prose*: the plan tells the user the folder
  "is a link to somewhere else", and §5.6's report records the same as the reason it survived. On a
  hardened machine an ACL on `%PROGRAMDATA%\Microsoft\Windows\WER` produces that sentence about an
  ordinary directory. The decline is correct either way, so nothing is at risk; the wording is
  wrong. **It is not fixed here because it is not this provider's wording.** The GPU shader caches
  and the Recycle Bin say the same thing from the same predicate, so the fix is a three-way answer
  from the seam and three providers updated together, and doing it in one would leave the three
  disagreeing about the same fact.

- **The ancestor check is made at plan time, and only the target is re-checked at execution.** Every
  directory between a declared root and a nested target is tested for being a link while the plan is
  built, and `DirectoryRemover` and `FileRemover` re-test the target itself before removing it — but
  not the path above it. A container that becomes a junction between the preview and the clean is
  therefore walked through, and the §5.6 negative still passes, because every survivor named for
  that root resolves through the link. This is the first provider with targets several levels below
  their root, so the exposure is new in degree rather than in kind. It is admin-to-admin — the only
  nested targets are under `C:\Windows` and `%PROGRAMDATA%\Microsoft`, both of which need
  administrator rights to write *and* to clean — and the correct fix is at the `DeleteStep`
  execution seam for every provider, which needs a boundary the remover is not currently given.

- **§9's closing sentence and this provider's name collide, and the position should be explicit.**
  §9 excludes "Windows component cleanup" and names `WinSxS`, `Windows\Installer` and the installer
  package caches, then generalises to "should not stake that trust on Windows servicing internals".
  A provider called *Windows servicing logs*, targeting `Logs\CBS`, is close enough to that sentence
  to deserve a stated reading rather than an inferred one. The reading taken: §9's subject is the
  component store and the installer database, whose failure modes are a broken uninstall and an
  unbootable rollback; a log is a record of servicing rather than a part of it, and removing one
  cannot break either. §3 places logs in Tier 3, which is where they landed, and that is the
  treatment §9's caution asks for short of exclusion. **If the maintainer reads §9 more broadly,
  this provider belongs beside `WinSxS` and the entry moves to §9 rather than shipping.**

- **Each target is removed whole, and the directory coming back is an assumption rather than an
  observation.** `DirectoryRemover` removes the root once it is empty, so `Logs\CBS`, `Minidump`,
  `RtBackup` and the rest go rather than being emptied. Every one of them is re-created by whatever
  writes into it — the servicing stack, the kernel's dump writer, the WMI autologger — which is the
  same shape `RecycleBinProvider` and `D3DSCache` already rely on, and which is what
  `WhatHappensOnNextUse` tells the user. **It has not been observed here**, because observing it
  means causing a bugcheck or an update. Nothing turns on it that a wrong answer would make
  dangerous: the failure would be a folder that stays missing until the next writer creates it, not
  a loss. Worth confirming on a machine that takes an update, and worth knowing that a
  keep-the-directory removal mode does not exist and would be the fix if the assumption is wrong.

Left out deliberately: `C:\$WinREAgent` and its siblings, which need the pending-restart check
established first and are still where this section left them; the Windows Search index, which needs
the service-control policy decided; and finding dumps by shape anywhere on the disk. On the last, one
thing the work adds: the *provider* shape here is a declared-path one, and a by-shape search is a
discovery one, so the two share nothing but the tier. Whoever builds it should read
`ChromiumUserDataDiscovery` rather than this.

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

### Servicing and update logs — Tier 3, measured at 64 MB ✅ done

**Outcome:** shipped as `WindowsServicingLogProvider`, sharing the declared-path machinery above and
almost nothing else. Four things this section did not anticipate:

- **It is a second provider rather than four more rows on the first.** "Crash dumps and servicing
  logs" needs the word "and" to describe it, which is G1's own test for two types — and more
  practically, a plan carries one tier and one `WhatHappensOnNextUse`, so a mixed provider could not
  have said two different things about two different consequences. Splitting also lets somebody clear
  the update logs without emptying the evidence of a crash, which is the likelier of the two wants.

- **Tier 3 here is the less comfortable half of the same correction.** A crash dump is obviously a
  record; a servicing log looks like scratch, every guide on the internet says to delete it, and the
  reclaim is routine. The argument does not change: the operation that wrote the log has finished and
  will not run again on request, so what Windows re-creates is the next log rather than the ones
  removed. The case where it bites is narrow and real — somebody diagnosing a failed update, or
  reading what `sfc` just wrote — and it is answered by the row being unticked and dated rather than
  by a tier that says nothing was lost.

- **The age had to come from inside the directory, and this is the first subject where that
  differs.** `RecycleBinProvider` could use a directory's own timestamp because nothing in a bin is
  ever rewritten in place. A log is appended to, which moves the file and leaves the parent
  untouched — so the directory alone would report a log being written at this moment as months old,
  which is exactly backwards for the one case the tier exists to surface. One level of children is
  enough for every location declared here.

- **§5.3's warning had to become unconditional, which no provider before this needed.** Every other
  §5.3 note is conditional on a named process being up. The WMI service holds `RtBackup` open and is
  always running, and it lives inside a shared `svchost` so there is no name worth giving the user.
  Reclaiming less than the size shown is therefore the *expected* outcome here, and a user who was
  not told that reads it as a failure. The plan says it outright, and `ConflictingProcessNames` still
  names `TiWorker` and `TrustedInstaller` for the servicing stack, which does have names.

One more thing the executor needed, and it is a general point rather than a local one. An unelevated
delete under `C:\Windows` is refused file by file, which arrives as exactly the skip a locked file
produces — so from the outcome the two causes are indistinguishable. Reporting only §5.3's "in use"
wording sends the user looking for a process to close that is not there, and asserting the elevation
cause would be wrong on an elevated run that hit a real lock. Both are named and neither is asserted.

`RtBackup` is the weakest member of the set and worth flagging for whoever revisits this. It is a
rotating runtime buffer rather than a historical record, which is the one entry here with a genuine
Tier 1 case; it is included because it is declared, sized and never pre-selected, so being
over-cautious about it costs the user a tick and nothing else.

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

Four further things the work settled that this section did not anticipate:

- **§5.1 has an answer here, and it is "no".** Windows ships `SHEmptyRecycleBin`, which takes a
  volume root — the grain this provider already works at — so the §5.1 question is live rather than
  vacuous, and every other provider in the product records its position on it. The position is that
  the preview outranks it: §7 makes the dry run the primary action, this plan names one directory
  per volume with a size and a date, and §5.6 asserts what survived beside it, none of which a call
  that names a volume and reports nothing back could support. §5.2 is the second reason, because the
  safety property is "this account's directory, never a sibling" and handing a whole volume to the
  shell puts that decision outside the code the rule is checkable in. The accepted cost is that the
  shell is not told what changed, so a Recycle Bin window left open may show a stale picture until
  it refreshes — not observed, and a stale picture rather than a stale deletion.

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
file **still exists and is still readable**.

§6's work has since made the *place* for it, without building any of it. Deletions now share a
`DeleteStep` base, and `CleanupPlan.TargetedPaths` and `NarrowedTo` select on that rather than on one
concrete kind — so this belongs beside `DeleteStep` and `RunCommandStep`, contributing no targeted
path and a §5.6 negative that asserts survival instead of removal. What remains is the design
decision itself, which should still be taken deliberately rather than discovered while writing a
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
| Chromium cache signature ✅ | Expected `ContentSignature` to have the shape. It did not — §4 records what the work needed instead | 0.8 GB |
| Crash dumps and servicing logs ✅ | Expected path-based plus elevation. Elevation turned out to be a claim `CleanupPlan` could not make, and `MEMORY.DMP` a step kind it did not have — §6 records both | 0.2 GB |
| Cargo, Go, Maven, vcpkg ✅ | Expected one class each on the npm and NuGet shape. Two wanted the declared-path shape instead, two moved to Tier 2, and the read-only trap turned out to be in the remover — §1 records all three | researched |
| pnpm and conda | A link-aware size, which open question 1 leaves unresolved. The MFT record carries the hard-link count; whether the fallback walk can answer at all is not established, and a provider whose number is right only under elevation is a poor citizen | researched |
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
5. **What does a non-deleting step look like in `CleanupPlan`?** Still open, and narrowed rather
   than answered. §6 added a second *deletion* kind for `C:\Windows\MEMORY.DMP`, and what that
   settled is where the boundary runs: everything that destroys a path derives from `DeleteStep` and
   is picked up by §5.2's assertions and §5.6's negative by construction, so a non-deleting step is
   a sibling of that rather than a variety of it. Cloud-sync dehydration is still the only known
   subject, so building it now would be the speculative generality G3 bans — the question is whether
   a second subject exists.
