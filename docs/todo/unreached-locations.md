# Unreached locations — what the shipped providers do not see

> **Status:** 🟢 ACTIVE — a researched candidate set, sequenced and under way. §1's Cargo, Go, Maven
> and vcpkg providers, §1a's pnpm and conda, §2's Unity, Rust, node_modules and virtual-environment
> providers, §4's Chromium application caches, §4a's Code - OSS editor caches and logs, §5's GPU
> shader caches, §6's crash dumps and servicing logs, §7's per-volume recycle bins and §12's
> Squirrel staging and superseded builds have shipped; everything else is unstarted.
> **Open questions 1 and 2 are answered** — see the foot of this document.
> Flip to ✅ COMPLETE and `git mv` into `done/` when the list is exhausted, or supersede it with a
> newer plan.

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
| Unity per-project `Library\` (7 projects) | 5.4 GB | 2 | Per-project build output; only `obj\` had a provider. **Shipped — see §2** |
| Store-Python `LocalCache\local-packages` | 5.4 GB | 3 | Same redirection blind spot; it is installed packages, not cache |
| `$Recycle.Bin` on non-system volumes | 3.6 GB | 3 | Cleaners empty `C:` only. `C:` held 0 bytes here. **Shipped — see §7** |
| `%LOCALAPPDATA%\NVIDIA\DXCache` + `GLCache` | 3.2 GB | 1 | GPU shader cache; no provider category existed. **Shipped — see §5** |
| Windows Search index (`Windows.db`) | 2.2 GB | 2 | Needs a service stop, so no cleaner attempts it |
| `C:\$WinREAgent` | 1.7 GB | 2 | Disk Cleanup's update pass does not remove it |
| .NET SDKs, 8 versions, one out of support | 1.8 GB | 4 | An uninstall, not a delete. §2 rules it out |
| Visual Studio `.vs\` per solution | 1.5 GB | mixed | Inside source trees, beside the `obj\` already walked. **Re-measured across 51 solutions, and the tier is a correction — see §2** |
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

**Outcome:** four of the six shipped in this phase — `CargoCacheProvider`, `GoCacheProvider`,
`MavenRepositoryProvider` and `VcpkgCacheProvider`, with `VcpkgDiscovery` split out beside the last
of them. pnpm and conda did not, and the row in the sequencing table was split rather than ticked,
both being blocked on the link-aware measurement open question 1 records. They shipped in the phase
after this one, and §1a below records what that took. Every one of the four here is
**researched rather than measured** — none of these
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

Left out deliberately, and each for a reason: pnpm and conda, which became §1a below;
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

## 1a. The two that needed a link-aware answer — pnpm and conda ✅ done

**Outcome:** both shipped, as `PnpmStoreProvider` and `CondaCacheProvider`, and **open question 1 is
answered rather than worked around** — the answer is at the foot of this document. The short form:
the unelevated walk can measure this, the MFT cannot do it better, and the two providers then took
*different* answers to the accounting question, which is the finding this section did not anticipate.

Seven things the work settled.

- **The measurement question and the reclaim question are different, and only one of them was open
  question 1's.** A perfect link-aware total for a store still is not the reclaim, because
  `pnpm store prune` removes only the unreferenced part of it. The two providers land on opposite
  sides of that and both are right: pnpm's estimate is Deguffer's own link-aware measurement of the
  store, because pnpm reports no figure of its own; conda's is conda's figure, because conda reports
  one and it is better than anything Deguffer could compute. Stating the question that way is what
  made it obvious that **conda never needed the scanner at all**, which is the next point.

- **Conda ships on PlatformIO's shape, and the phase was two phases.** `conda clean --dry-run --json`
  reports `total_size` per category in bytes, and conda's own sizing skips any file with more than
  one hard link — the check is `if stat.st_nlink > 1: raise NotImplementedError` in its
  `main_clean.py`, which drops the file from the total. So what the dry run reports is exactly what
  the clean removes, already link-aware, computed by the tool that owns the records. Deguffer shows
  that and measures nothing of its own except the channel index cache, which the report lists by path
  and never sizes. Open question 1 gated **pnpm alone**; conda was only ever waiting behind it by
  association. Where the dry run cannot be read, the plan offers nothing, because the only substitute
  figure is the one that counts every package the environments still link.

- **A tool's figure and Deguffer's measurement cannot be subtracted from one another, and the
  executor was doing exactly that.** `PlanExecutor` computed reclaim as the step's estimate minus a
  re-measurement of `MeasuredPaths`. That is sound for every provider whose estimate *is* its
  measurement of those paths, which was all of them until now. For conda the estimate is a
  prediction and the re-measure is a measurement, so a successful clean would have reported a
  negative delta and the "the cache grew since the preview" sentence. `RunCommandStep.MeasuredBefore`
  carries Deguffer's own plan-time probe of the same paths, and the executor prefers it where it is
  present. Null everywhere else, so nothing changed for the other seven command providers.

- **The tier moved for conda, and this section's stated reason for it was wrong.** Tier 2 stands, but
  "re-creating an environment is a download rather than a rebuild" describes an operation the command
  never performs — `conda clean` touches no environment, and every package an environment links
  stays. The two reasons that survive are the size of the re-download, which is Tier 2's own
  definition, and conda's documented warning that its unused test cannot see an environment linked by
  *symlink*. The second is the honest one: a rule that can be wrong in the destructive direction
  belongs at the tier nothing is ticked at. That is the third phase running in which the survey's
  tier was right and its argument was not, and the pattern is worth naming — a proposed tier is a
  conclusion, and the work has to re-derive it rather than inherit it.

- **§5.1's answer is "yes" twice, and the interesting half is which flags to leave off.** Both tools
  ship a command, so neither provider deletes a path. What each *declines* to pass is the part that
  needed deciding, and both are written into the class. pnpm's `prune --force` means "also remove
  alien files", directories the package manager did not create — deleting what no rule can name is
  §5.2's whole subject, so force is never passed. conda's `--all` sweeps in `--logfiles`, and a log is
  a record of something that already happened, which §6 of this document has twice argued is Tier 3;
  a Tier 2 plan must not carry one, so the categories are named individually. `--force-pkgs-dirs` is
  never passed at all, conda's own help saying it breaks environments linked back to the cache.

- **A configured root reached by a *command* is a smaller hazard than one reached by a deletion, and
  the reason is worth writing down.** The last phase closed four holes of the shape "a string from an
  environment variable used before anything established what it was", and both providers here take
  such strings — `pnpm store path`, `PNPM_HOME`, `CONDA_EXE`, and three path lists out of
  `conda info --json`. Every one goes through `LongPath.Configured`. But neither provider needs the
  `LongPath.Contains` refusal Maven and vcpkg carry, and the distinction is structural rather than a
  judgement call: those two turn a configured root into a `DeleteDirectoryStep`, and these two turn
  it into a `RunCommandStep` whose paths are a measurement probe. A hostile value here produces a
  wrong *number*, never a Deguffer deletion. Both suites assert `TargetedPaths` is empty, which is
  the assertion that keeps it structural.

- **Conda turned out to be installed on the audited machine, and the survey's premise for this row
  was wrong.** This section said neither tool was present. Miniconda was, at
  a machine-wide location no `PATH` entry pointed at, which is exactly why the provider looks in
  three places rather than one. So conda is **partly measured rather than purely researched**:
  `conda info --json` and `conda clean --dry-run --json` were both run against the real tool, and
  their output matches what `CondaReport` parses, field for field. What could not be observed is a
  populated cache — all three `pkgs` directories it reported were absent, so the provider produced
  its "cached nothing yet" empty plan and the row rendered at zero. The sizes in this section remain
  vendor documentation. pnpm is not installed and stays entirely researched.

One residual the work left behind, since **fixed** — and the fixing turned up something larger
underneath it. Both are recorded here rather than removed, because the shape of the mistake is worth
more than the correction.

- **A command step's reclaim reads zero on an elevated run, for every §5.1 provider.** ✅ fixed.
  `PlanExecutor` reports what a command freed as the plan-time figure minus a re-measurement of the
  same paths. The re-measurement goes through the provider's own scanner, and `DirectoryScanner`
  holds its volume index until something calls `Invalidate` — which happens once, at the start of a
  planning pass. So where the fast path is in play, both readings come from the same pre-command
  snapshot, they cancel, and the step reports nothing reclaimed after a clean that freed gigabytes.
  That is §5.4's own stated failure, "the user will prune, see no change, and lose trust in the
  tool", arriving by a different route.

  **Observed rather than reasoned about**, on a real volume under elevation: a 10 MB tree measured
  through the index at 10,485,760 bytes, deleted, then measured again through the same scanner at
  10,485,760 bytes. The identical figure, not a rounding difference.

  This section proposed two fixes and both were wrong to reach for. Rebuilding the volume index
  after each command drops *every* volume and costs seconds per command step, several times over in
  one run. Taking the after-measure by walking is right, but the stated cost — "give up the fast
  path exactly where the tree is largest" — is not a real cost: the after-measure runs *after* a
  successful eviction, so the tree it walks is the empty one. The expensive walk happens only when
  the command freed nothing, which is the case Deguffer least needs to be quick at.

  What landed is that second option, stated as a rule rather than a route.
  `IDirectoryScanner.MeasureFromDiskAsync` is a measurement that must not come from a snapshot, and
  the executor's after-measure uses it. A separate member rather than a flag, because a figure
  subtracted from an earlier one has to come from the disk — a property of the question, not a
  tuning option a caller may get wrong. The two scanners that remember nothing between calls answer
  it with the walk they already do, so pnpm's link-aware sum is untouched.

- **§5.5's fast path had never engaged on a real volume, which is why the residual above could not
  be reproduced at first.** ✅ fixed, and this is the larger finding.

  `MftVolumeIndexBuilder` abandons the whole volume when any record is unreadable, and
  `MftRecordParser` calls a record unreadable when it is in use, carries no `$FILE_NAME` and points
  at no `$ATTRIBUTE_LIST` — on the stated grounds that "no healthy volume produces this". NTFS
  reserves the first sixteen records of every volume for its own metadata and holds 12 to 15 back
  for future use, marked in use and given no name. Every NTFS volume produces four of them.

  Measured on a real volume, elevated, with the builder instrumented to log rather than bail:
  `UNREADABLE` at records 12, 13, 14 and 15, and nowhere else in 3,038,208 records. The build
  aborted at the first, the index never existed, and every path on an elevated run fell back to the
  walk — including a real npm cache, whose reclaim was therefore *correct*, because the walk is
  genuinely fresh. Elevation bought nothing at all, and reading the MFT is the entire reason the app
  offers to elevate.

  **The fixture is why this stayed green.** `MftFixture` zero-filled records 0 to 4 and numbered
  fixtures from 6, and a zero-filled record reads as "not in use" and is skipped — so a suite full
  of MFT tests proved the reader worked on a volume nobody has. The fixture now writes 12 to 15 the
  way NTFS does. The lesson generalises past this one reader: a fake that models an idealised
  version of the thing under test is worth less than no fake, because it produces confidence rather
  than doubt.

  The two defects had to be fixed in this order, and only in this order does either matter. Before
  the first, the reclaim figure was right by accident; after it, the fast path serves planning and
  the stale snapshot becomes reachable. Both were then confirmed together on a real elevated volume:
  the plan-time reading came from the table, and the step reported 10,485,760 bytes rather than 0.

One thing the deduplication turned up on its way past, worth recording because of how it was
found. **§5.3's "access denied is normal, skip silently" had no test at all, in either scanner** —
removing the catch filter outright left every test green, so the rule could have been deleted and
nothing would have noticed. It went untested for as long as it did because it lived in two places
and belonged to neither; one shared seam made it one testable thing, and it is tested now. The
fixture is a directory the running account denies itself the right to list, which needs no
elevation: the deny goes on the DACL, the account stays the owner, and an owner may always take it
off again. A first attempt was abandoned on the belief that restoring it needed a privilege an
ordinary process lacks. That was wrong — `SeSecurityPrivilege` gates the audit list, not the
permissions — and the lesson generalises past this one test: a safety rule left untested because
the fixture "cannot be built" deserves the second look, because the reason is sometimes a mistake.

Once the fast path was reachable, it was watched running. **What it does on a real machine is not
what §5.5 assumes**, and the numbers are recorded here because no fixture can produce them.

- **The table answers most locations and roughly half the bytes.** Across the 328 paths one
  planning pass measures on this workstation, plus `C:\`, `C:\Windows`, `C:\Windows\Logs`,
  `C:\Windows\Logs\DISM`, `C:\Program Files` and the two `dotnet` locations, 335 in all: 322
  answered from the table and 13 declined. Every decline is on `C:`, where the ratio is 35 answered to 13, and the
  declines are the large ones — `.nuget\packages` at 7.97 GB, the NuGet v3 cache at 2.48 GB, and
  the npm cache. The answered paths total 11.88 GB and the declined 11.84 GB.

- **Every decline has one cause, and it is not the one the code most guards against.** All 13 are
  `SumSubtree` meeting a record whose size the table did not establish. Not one is a path the index
  could not resolve, and not one is a reparse point. Re-reading 400 of those records raw: **400 of
  400 carry an `$ATTRIBUTE_LIST` and no unnamed `$DATA` in the base record**, so the size lives in
  an extension record `MftRecordParser` does not follow — which is the compromise its own comment
  names. The volume holds 18,579 such records among 2,556,460 present ones, 0.73%, and they are
  enough to poison a subtree of two: `C:\Windows\Logs\DISM` declines on one record out of two,
  and 352 MB of Playwright browsers on one out of 77.

- **Where the table does answer, it agrees with the walk exactly.** Across 322 answered paths the
  table's logical total and the walk's were equal to the byte, every time. The fast path's numbers
  are right; it is their availability that is the problem.

- **An elevated preview is slower than an unelevated one, end to end.** 28.8 seconds elevated
  against 15.5 unelevated cold, and 28.0 against 17.1 warm — the elevated run second in both cases,
  so with the warmer cache. The cause is legible: building the index cost 9.9 seconds across seven
  volumes, and walking every path it then answered for would have cost 1.24 seconds (1.09 on `C:`
  over 45 paths, and 0.15 over the 282 on the source volume). Five of those volumes were indexed at 0.47 to 0.72 seconds
  apiece to measure one 129-byte Recycle Bin each, because that is the only location on them any
  provider names.

  **The half that does get quicker is discovery**, which is the walk §5.5 was really written
  against: finding every sought directory inside the approved source root took 5.34 seconds by
  walking and 2.71 through the index. That is the one speed claim in the UI that survived the
  measurement, and it is the one the source-tree plan note makes.

  **This does not contradict §5.5, and it is worth being exact about why.** The founding audit's
  "over ten minutes across a handful of profile subtrees" was measured on *naive recursive*
  enumeration. What ships is `BoundedFileWalk`, the bounded parallel pool §5.5's own second bullet
  prescribes, and the 15.5 seconds above is that. The two figures are not two machines disagreeing;
  they are two algorithms. What the measurement does show is that the prescribed fallback turned out
  fast enough that the route it was written to fall back *from* no longer pays for itself here — so
  the premise stands and only the sentences promising *this* user a quicker scan were changed.

- **Elevating made seven real locations unselectable, until the reported axis changed.** A file
  small enough to live inside its own MFT record occupies no clusters, so the table reports zero
  allocated for it. `ScanSize.Reclaimable` was Allocated, `CleanupStep.EstimatedBytes` reads it, and
  a step with nothing to reclaim cannot be ticked — so every per-volume Recycle Bin came back at 0
  bytes on an elevated run and at 903 on an unelevated one. `Reclaimable` is now `Logical`; its own
  doc comment carries the three measurements behind that, including what it would cost to teach the
  walk to report allocated bytes instead (`GetCompressedFileSize` is 1.1x to 5.4x a length pass and
  returns the length again for anything uncompressed; the call that does answer needs a handle per
  file and took 16.7 seconds over a 426 MB cache against 107 milliseconds).

- **The index route for discovery was checked against a real volume, not a fixture.** An elevated
  pass and an unelevated one over the same approved roots produced byte-identical target lists, 328
  paths each. `SourceTreeBoundary` was then put through the cases `RouteAgreementTests` covers
  synthetically, against the real `C:` index: the raw index returned five `obj` directories inside
  the approved root and the filter kept two, dropping one nested inside another candidate, one under
  `.git` and one under `node_modules`. A junction never appeared as a raw match at all — the index
  cannot build a path through a reparse point — and neither did an `obj` in a sibling directory
  outside the approved root. The one intended disagreement held: below a directory the account may
  not list, the index offers a candidate and the walk finds nothing.

**What is left, and why it is not fixed here:** following `$ATTRIBUTE_LIST` to the extension record
that holds a file's `$DATA` is the root-cause fix for every decline measured above, and it is a
piece of NTFS work with its own fixture requirements rather than a correction — see
[after-the-scanner.md](after-the-scanner.md) items 6 and 7, which also carry the per-volume index
cost. Item 8 there carries a third thing this pass established and did not fix: a root probed by
name cannot tell "not there" from "I was refused", so a provider reports a cache as not installed
when the directory is on disk with content in it.

### pnpm — Tier 1, researched ✅ done

`pnpm store path` locates it and `pnpm store prune` evicts only what no project on the machine still
references. A better eviction than npm's, because it is selective rather than total.

**Measuring it the ordinary way reports a number that is not true.** pnpm hard-links store contents
into every `node_modules` that uses them, so one set of blocks appears under many paths. Summing
file lengths counts each copy, and the disk gives back only the blocks whose last link went away.
This is §5.4's lesson in a different costume: report what will actually be freed, or the user prunes
4 GB and watches free space move by 400 MB.

**The MFT was the wrong place to solve it**, which this entry had backwards. The record does carry a
hard-link count, but the table is readable only under elevation, so a figure taken from it would
disagree with the unelevated figure for the same store — and §6.3 makes unelevated the ordinary run.
`HardLinkAwareScanner` asks the file itself instead. Open question 1 records what that cost and what
it does not cover.

### Conda — Tier 2, partly measured ✅ done

Anaconda's own documentation puts the `pkgs` directory at tens to hundreds of gigabytes.
`conda clean` removes tarballs, unused cached packages, the index cache and the source cache, and
`--dry-run --json` reports the figure without acting — the same relationship PlatformIO's
`prune --dry-run` already has, and the shape this provider ships on.

Tier 2 rather than Tier 1, but **not** for the reason first written here. See the outcome above:
the command re-creates no environment, so the tier rests on the size of the re-download and on
conda's own warning about environments linked by symlink.

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

## 2. Per-project build output — Unity, Rust, node_modules and .venv ✅ done

The seam already exists. `DotNetObjProvider` walks source roots through `SourceRootStore`, and the
age column built for the cpptools workspace databases is exactly the signal these need. What is
missing is the other kinds of build directory.

**Outcome:** four kinds shipped — `UnityLibraryProvider`, `CargoTargetProvider`,
`NodeModulesProvider` and `PythonVirtualEnvironmentProvider`, all on a shared `BuildDirectoryProvider`
reading a declared `BuildDirectoryKind`. **Open question 2 is answered rather than worked around** —
the answer is at the foot of this document, and it is the larger half of what this phase was. Visual
Studio's `.vs` did not ship and the row was split rather than ticked; `dist` and a Dart `build` are
both declined outright. Every reason is below, and the `.vs` one is the finding of the phase.

Ten things the work settled.

- **A live tree can be detected, unelevated, and the name check it replaces was wrong in both
  directions.** §5.3's exclusion asked "is a process called `devenv` running", which vetoes every
  project on the disk when any one is open, and misses the case entirely when the process holding a
  directory is not called what a reader would expect. Both halves were observed rather than reasoned
  about: a live Visual Studio solution's `.vs` index is held open by `DevHub.exe`, a service host,
  while `devenv` holds nothing inside `.vs` at all. `ILiveTreeInspector` asks about a *path* instead,
  and a directory it reports live is vetoed rather than warned about — the difference between a cache
  a tool is still writing to and a build directory removed under a live editor is the difference
  between a slower next use and losing the afternoon.

- **The Restart Manager answers for a file and refuses a directory, which is why there are two
  mechanisms rather than one.** `RmRegisterResources` returns `ERROR_ACCESS_DENIED` for a directory
  path — observed on three separate directories — so there is no way to ask "is anything under this
  tree open". What it does answer, unelevated, is who holds a named file, correctly and in about ten
  milliseconds once the session is up. That makes it the §5.1-shaped signal: the provider names the
  file its tool locks, and Unity's `Library\UnityLockfile` is the case. Existence is not the test,
  because a crashed editor leaves one behind for ever; being held open is.

- **The other two signals come from the process table, and they are what answer for a directory.** A
  running program whose executable is inside the directory is an activated `.venv` or a binary
  started from `target\debug`. A running program whose working directory is inside the *project* is a
  build in flight, a shell, or an editor with the solution open — Visual Studio's working directory
  is the solution folder, which is how an open solution is recognised without knowing any process
  name. One pass over the whole table costs about 30 ms for ~500 processes, of which ~360 could be
  opened at all, and needs no elevation.

- **The working directory has no documented accessor, and the mitigation is the interesting part.**
  It is read out of the process environment block at offsets Windows does not promise to keep. A
  layout that moved would produce nonsense matching no directory, which reads as "nothing is using
  this" — the one wrong answer that costs somebody their work. So the offsets are checked against
  Deguffer's own process, whose working directory is already known, and a mismatch turns the
  mechanism off and says so. `LiveTreeFindings.Complete` is what carries that outward, and it is the
  same distinction §5.5 draws for the measurement fallback: a safeguard that could not run must not
  look like a safeguard that found nothing.

- **The veto can miss and must never fire wrongly, and that asymmetry is what sets the shape.** Every
  signal is positive evidence, so a directory reported live is one. The reverse does not follow, and
  review found three gaps rather than the one this phase started with. A compiler holding a file deep
  inside a tree that is neither its own image nor its working directory is invisible to all three
  signals. **A process running elevated or as another user cannot be opened at all from an unelevated
  Deguffer**, so an elevated build is invisible too — measured at 143 unopenable and 46 more readable
  only in part, out of 457 processes, of which three were ordinary elevated user applications in the
  interactive session. And the Restart Manager refuses a path past `MAX_PATH` in *either* form, so
  the lock-file signal cannot run that deep at all; that one is reported rather than silent, because
  the refusal turns the findings incomplete. None of the three closes without elevation, and §6.3
  makes unelevated the ordinary run. So the veto is one input and age is the other, exactly as this
  section predicted — "age is the whole decision" is not quite right, but "the veto answers *now* and
  age answers *dormant*" is.

  **The middle two are stated rather than warned about, and that was a decision.** A note saying "a
  process running as administrator would not be seen" is true of every unelevated run on every
  machine, so a plan carrying it would carry it always, and a warning that never varies is one the
  user learns to read past — taking the one that does vary with it. `LiveTreeFindings.Complete`
  therefore reports a *mechanism* failing, and the standing limits of running unelevated are written
  into the interface, this document and the user-facing guide instead.

- **Age is read from the build directory's immediate entries, and both other candidates were
  rejected for stated reasons.** Not the directory's own timestamp: that moves only when an entry is
  added, removed or renamed, so a project rebuilt daily reports the date its output layout last
  changed — the servicing-log provider met the same trap from the other side, and it applies here.
  Not the source beside it either, which is the tempting answer: age is asked in order to price a
  deletion, what a deletion costs is a rebuild, so "when was this last built" is the question and
  "when was this project last edited" is a different one. Reading the source would also mean walking
  the tree this exists to avoid walking. `DotNetObjProvider` already had this rule; it is
  `BuildDirectoryAge` now, shared by all six. **Half of that reasoning was later found to be an
  argument against the wrong thing — see the audit below, where the rule became the newest of the
  directory *and* its entries, in `DirectoryAge`.**

- **The `node_modules` collision needed no exception, and seeing why took longer than fixing it.**
  `SourceTreeBoundary` refuses to enter `node_modules` because walking one costs hundreds of
  thousands of entries. A search that is *looking* for `node_modules` stops at it without descending
  anyway, because everything below a candidate belongs to that candidate — so the sought-name stop is
  a strictly stronger rule than the boundary, and the two never disagree. What did have to change is
  that "inside another candidate" is a property of the whole name set rather than of one name: a pass
  that knows about `node_modules` never offers the `build` directory of a package inside one, and a
  pass that does not will walk straight in. Both routes are handed the same set for that reason, so
  elevating cannot change which *rules* Deguffer applies. **It does change the reach, which this
  sentence originally claimed it did not — see the audit below.**

- **§5.1's `cargo clean` was considered and declined, and the reasons are in the class.** It is a
  per-project command run in the project's own directory, so it means one subprocess per Rust project
  on the disk, each able to hang, against a per-step selection model where the user picks a handful.
  §5.1's actual argument — that the tool reaches locations we do not know about — buys almost nothing
  here, because with no configuration `cargo clean` removes the very directory discovery already
  found. What it reaches and this does not is a `target` relocated by `CARGO_TARGET_DIR`, and that
  case is invisible to a path-based provider rather than mishandled by it. A machine whose Rust
  toolchain has been uninstalled still has its `target` directories, and a command-based provider
  could not touch them at all.

- **Cargo's tier moved, which is the fourth phase running in which a proposed tier or its argument
  did not survive.** This section proposed Tier 1. Restoring a `target` is not a slower build, it
  *is* the build: every dependency compiled from source, per profile and per feature set, which is
  where the five to twenty gigabytes came from. That is vcpkg's argument from the previous phase — a
  cache whose entries are recovered by compiling rather than downloading — with more force, because
  there is no cache to fall back on. Unity's proposed Tier 2 held *and* its stated argument held,
  which is the first time in four phases. The other three are Tier 2 on the ordinary reading.

- **Consent had to move with the code, and it is one sentence rather than a mechanism.** The Settings
  copy said Deguffer looks inside approved folders "for .NET intermediate build output (obj)". That
  is a specific promise, and approving a folder under it is not approval to find `node_modules`. The
  copy now names every kind, in the same change that widened the search. A second consent list was
  considered and rejected: the *scope* is unchanged — the same folders, nothing new outside them —
  and only the kinds found inside widen, which §7's preview-first flow already shows the user before
  anything is removed. There is no way to grandfather an existing approval, because the roots file
  records only paths, so an existing approval is re-read under the new sentence and can be removed.

- **A `build` beside a `pubspec.yaml` shipped and was then withdrawn, and the reason generalises.**
  It was written, tested and reviewed before the question that killed it got asked: what, inside that
  directory, says the toolchain made it? Nothing. `pubspec.yaml` and `.dart_tool` are both facts about
  the *parent* — a Dart package is here, and `pub get` has run — and a pure Dart package does not
  always produce a top-level `build` at all, so the conjunction is satisfied by projects whose `build`
  is somebody's own. Unity's `Library` survives the same question because its parent's identity
  *implies* the child: a directory holding `Assets`, `Packages` and `ProjectSettings` is a Unity
  project, and Unity always creates `Library` in it. Checking real Flutter projects on this machine
  settled the last hope: `.last_build_id` is present in one project's `build` and absent from
  another's, so there is no marker to require. **Recognition by a directory's name plus its
  neighbours, with nothing inside it, is the shape to refuse** — it is §5.2's dangerous direction
  wearing a conjunction.

Two defects the work turned up, neither of them in a provider.

- **`Any(predicate)` short-circuits, so a provider declaring two directory names searched for one.**
  `SourceDirectoryDiscovery.Include` registered names with `names.Any(_names.Add)`, which stops at
  the first name that is new. Python declares `.venv` and `venv`; only `.venv` was ever searched for.
  Caught by the test for the second name, which is exactly the case a single-name provider would
  never have exercised.
- **The unelevated run announced the walk twice.** Discovery says it walked, and the measurement says
  it walked, in two wordings one under the other. They are different facts and a fast measurement can
  follow a walked discovery, but read together they look like a defect, so the discovery sentence is
  now left to the measurement's where that one is already there. Found by driving the real window,
  not by reading the code.

### The route and age audit — three claims re-examined, two of them wrong ✅ done

A later pass went back over what §2 had just shipped and asked, of each safety-shaped claim,
whether the consequence actually follows rather than whether the code is shaped as described. Two of
the three did not.

- **The boundary asserted an invariant it does not hold, and the disagreement is real.**
  `SourceTreeBoundary` opened by saying the walk and the volume index cannot become "two different
  answers to the same question, chosen by whether the user happened to elevate". Measured against a
  real directory the account is denied the right to list: the walk returns **nothing** below it,
  because `ChildDirectories.Under` gives nothing rather than a partial view, and the index returns
  the `obj` inside it. The candidate is not a degraded one either — denying the right to *list* a
  directory leaves the right to traverse it, so a full path still resolves, the project around the
  candidate is readable, recognition succeeds and the directory measures. An elevated run genuinely
  offers what an unelevated one cannot find.

- **The disagreement is accepted, because it is reach rather than licence.** Making the two agree
  would mean asking, per candidate ancestor, whether this process may list it — a directory
  enumeration each, on the one route that exists because enumeration is too slow (§5.5) — and the
  answer would come back under the *elevated* token, describing a walk that never happened. It also
  buys nothing: the boundary's rules are applied by name, a name is readable whatever the ACL says,
  and an indexed candidate still has to be inside an approved root, recognised by the project around
  it (§5.2), cleared by the live-tree veto, and shown in the preview. An elevated run reaching more
  of the folder it was asked to look inside is §5.5's intent. What changed is the claim: the class
  now states what it guarantees and what it cannot, and `WouldBeFoundByWalking` is
  `IsInsideTheSearch`, because a method that cannot answer for the walk should not be named for it.
  `RouteAgreementTests` pins all three parts of that argument in one denied tree, because each is
  worthless alone: the walk finds nothing, the index still refuses everything the boundary refuses,
  and what the index offers is a whole candidate rather than a broken one.

- **Four answers to "how old is this" were three copies of one rule plus one honest exception, and
  the shared rule was missing half of itself.** `BuildDirectoryAge` argued at length that a
  directory's own timestamp lies, because it moves only when an entry is added, removed or renamed.
  That is an argument against using it *alone*, and it had been applied as an argument against using
  it at all — so the rule caught a file rewritten in place and missed everything the directory
  catches. Measured on this machine: a directory whose entries are 400 days old but which was itself
  written a moment ago reported **400 days**, and one emptied an hour ago reported **unknown**. Both
  errors point the same way, and it is the one that invites a deletion. The rule is now the newest of
  the directory *and* its immediate entries, one level, in `DirectoryAge` — and `DeclaredLocations`
  and the VS Code cpptools provider call it instead of carrying their own copies. A directory whose
  entries cannot be read reports **no age at all**, which is the one place the consolidation could
  have gone wrong quietly: a refusal leaves only the directory's own timestamp, and that is the half
  that reads older than the truth. The live subject is a servicing log directory the account may not
  list, whose traces are rewritten while the parent's timestamp sits still — a Tier 3 row, whose loss
  is permanent, carrying an age that invites the deletion.

- **The Recycle Bin was the one genuine exception, and it stays as it was.** It asks the same
  question, and its own timestamp is already the whole answer: nothing in a bin is rewritten in
  place, so no entry can be newer than the directory. Reading the entries would enumerate everything
  the user has deleted on the volume to arrive at a timestamp already in hand, and would read the
  wrong dates anyway — Windows preserves each deleted file's own timestamps, so those say when the
  files were last edited rather than when they were thrown away. That is now stated in the code
  rather than left as a difference the next reader has to account for.

- **A safety filter was one edit to an unrelated project file away from disarming itself.**
  `LiveTreeInspector` drops Deguffer from the process table so its own working directory does not
  veto every project below it, and it identified Deguffer by comparing each process's image path to
  `Environment.ProcessPath`. That is only Deguffer's own executable while the app ships
  self-contained, which is a property of `Deguffer.App.csproj`. Framework-dependent, or started
  through a shared host, `ProcessPath` is `dotnet.exe` and every other `dotnet.exe` on the machine
  matches it — a build in flight included, which is precisely what the veto exists to catch. The
  filter now compares process ids, which no build setting can reach. What that gives up is a second
  Deguffer running from inside the same source tree, which then vetoes it: the over-reporting
  direction, where the wider rule's failure handed a live project to a deletion.

- **One gap this pass found and did not close.** ✅ closed since, and it was wider than described
  here. A walk that is refused a directory told nobody: §5.3's "skip silently" working as designed
  for a measurement, but for *discovery* it meant part of an approved root went unsearched with the
  plan saying nothing about it. Closing it did need `ChildDirectories.Under` to distinguish "empty"
  from "refused", and every one of its ten callers did have to answer for it.

  What this entry understated is what the other nine callers were doing. For four providers the plan
  did not merely say nothing — it said something **false**, and the same provider's own presence
  probe contradicted it within one planning pass. Listing a directory and traversing it are separate
  rights, so a probe by full name answers correctly through a parent that refuses to be enumerated:
  `RecycleBinProvider` could report "your bin is on D:" and then, one method later, "no volume on
  this machine holds a Recycle Bin for this user". `ChromiumCacheProvider`, `CargoCacheProvider` and
  `GpuShaderCacheProvider` had the identical shape. Three more — Gradle, Playwright and the VS Code
  cpptools cache — emitted no note at all and fell through to a plan with zero steps, which the shell
  rendered as "Already clear", a claim with less behind it than the sentences above.

  Neither §5.2 nor §5.6 was ever breached by this. Classification iterates the child list, so an
  empty one recognises nothing and declines nothing; `PlanVerifier` probes every protected path by
  full name, which answers correctly through a refused parent. The defect was always in what the
  user was told, which is why it read as cosmetic and was not.

### Unity `Library` — Tier 2, measured at 5.4 GB across 7 projects ✅ done

Unity regenerates `Library` from the project's assets and settings, so nothing is lost. Tier 2
rather than Tier 1 because the regeneration is a full asset reimport, which on a large project is
tens of minutes. The largest single one measured 1.59 GB.

The recognition rule is strong: a directory named `Library` whose parent also holds `Assets`,
`Packages` and `ProjectSettings` is a Unity project. That is a content signature over the *parent*,
and `ContentSignature` already makes that kind of judgement for the cpptools workspace databases.

**Re-measured while this shipped, and the survey's premise about the machine was half wrong.** Unity
Hub is installed here and no editor is, so `Library` is *measured* — 5.26 GB across the same seven
projects, largest 1.67 GB — while nothing about a running editor could be observed. That is why the
lock file goes through the Restart Manager rather than being tested for existence: the rule is
provable against a file this test holds open, with no Unity anywhere.

### Rust `target` — Tier 2, researched ✅ done

The largest per-project directory in common developer use, routinely 5 to 20 GB per workspace,
because every dependency is compiled per profile and per feature set.

**Tier 2 rather than the Tier 1 proposed here, and path-based rather than the §5.1 command.** Both
corrections are argued in the outcome above. Recognised by a sibling `Cargo.toml` *and* by
`CACHEDIR.TAG` inside, which Cargo writes itself — the manifest says a Rust project is here, and the
tag is the part that says Cargo made this directory. No Rust toolchain is installed on this machine,
so the sizes remain vendor documentation and community report, and the rules are proved through a
synthetic tree rather than against a real `target`.

### Visual Studio `.vs` — measured at 1.45 GB across 51 solutions, **split out, not shipped**

Per-solution IntelliSense database, browsing data and editor state, hidden beside the `.sln`.

**This entry is wrong, and correcting it is the most valuable thing this phase produced.** It
proposed Tier 1 with one Tier 3 child, `.suo`. What is actually in `.vs` on the surveyed machine:

| Child | What it is | Tier |
| --- | ---: | --- |
| `<solution>\v<N>\` | 406 MB. Holds `.suo` (86 MB), `.wsuo`, `DocumentLayout.json` — open documents, breakpoints, window layout — beside genuinely disposable browse databases, `ipch` and a 236 MB `Server` directory | mixed |
| `<solution>\lut\` | 350 MB of code-coverage data — a record of test runs that already happened | 3 |
| `CopilotSnapshots\` | 265 MB of file-state snapshots | 3 |
| `ProjectEvaluation\` | 150 MB of MSBuild evaluation cache | 1 |
| `<solution>\FileContentIndex\` | 144 MB of `.vsidx` index files | 1 |
| `<solution>\copilot-chat\sessions\` | 71 MB of **AI conversation history**, megabytes per session | 3 |
| `<solution>\CopilotIndices\` | 36 MB, an index over the code — probably Tier 1, not established | ? |
| `<solution>\DesignTimeBuild\` | 24 MB of design-time build cache | 1 |

**Roughly a quarter of `.vs` by size is Tier 3 material this entry did not know existed**, and
`copilot-chat\sessions` is §3's founding mistake exactly — Visual Studio Code's `workspaceStorage`,
dominated by chat history, in a second costume one directory away from a folder every developer would
call disposable.

Two smaller corrections come with it. The version directory is not `v17`: `v14`, `v15`, `v16`, `v17`
and `v18` were all present here, because Visual Studio 2026 writes `v18`, so a rule hard-coded to
`v17` would have walked past a `.suo` it thought it had accounted for. And `.vs` has children at two
different levels — `ProjectEvaluation`, `CopilotSnapshots`, `VSWorkspaceState.json` and
`slnx.sqlite` sit directly under `.vs`, while the rest sit under a per-solution directory — so this is
a nested recognised-children problem rather than the flat one every other provider has.

That is a different shape of work from the five that shipped: those remove a whole directory the
project regenerates, and this one has to name disposable *children* inside a directory that is
mostly not disposable. It is its own row in the sequencing table now.

### The rest of the shape — `node_modules` and `.venv` ✅ done; `dist` and Dart `build` declined

Outside Unity, the audited machine held 1.24 GB in a `dist`, 1.17 GB in a Dart `build`, 803 MB in a
Python `.venv`, and 681 MB across two `node_modules`. Each is regenerable from a manifest beside it,
and each is worthless in a project nobody has opened for a year.

**Two shipped and two are declined.** `node_modules` requires `package.json` *and* a lock file,
because "regenerable" is a claim that reinstalling returns what was there and only a lock file makes
it true. A virtual environment requires `pyvenv.cfg` inside *and* a dependency manifest beside,
because without a manifest the environment is the only copy of its own contents and removing it
destroys information rather than freeing space.

**`dist` and a Dart `build` are declined for the same reason, and it is the one this section did not
anticipate.** Both would be recognised by their name plus what stands beside them, with nothing
inside either directory that the toolchain alone writes. For `dist` that is obvious: it is a build
script's output on some projects and a hand-curated folder of releases on others. For `build` it took
the review to see — `pubspec.yaml` and `.dart_tool` prove a Dart package is present and that
`pub get` has run, both facts about the parent, and unlike Unity the parent's identity does not imply
the child exists or is the toolchain's. That is 2.4 GB of the measured 3.2 GB left on the table,
deliberately, and the rule it establishes is worth more than the reclaim: **a directory outside a
tool's own root needs evidence from inside itself, or a parent whose identity implies it.**

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

## 4a. What a Code - OSS editor keeps that Chromium's names do not cover ✅ done

**Outcome:** shipped as `VsCodeCacheProvider` (Tier 1) and `VsCodeLogProvider` (Tier 3), over one
`VsCodeUserDataDiscovery`. Measured on one Windows 11 workstation, on a machine §4's provider had
already been run against.

| Path | Size | Tier | What it is |
| --- | ---: | --- | --- |
| `%APPDATA%\Code\WebStorage\<n>\CacheStorage` | 982.6 MB | 1 | Cache-storage responses, one folder per webview partition. The same content as `Service Worker\CacheStorage`, which §4 already recognised, under a different parent |
| `%APPDATA%\Code\CachedExtensionVSIXs` | 775.6 MB | 1 | Downloaded extension packages kept after installation. 22 files, two of them successive builds of one extension at about 103 MB each |
| `%APPDATA%\Code\CachedData\<commit>` | 286.8 MB | 1 | The V8 compiled-code cache, one folder per editor build. 16 build folders present, of which at most one belongs to the installed build |
| `%APPDATA%\Code\Crashpad` | 152.9 MB | 3 | The crash reporter database |
| `%APPDATA%\Code\logs\<timestamp>` | 141.7 MB | 3 | One folder per editor session |

§4's provider already reaches this folder — it holds `Local State` — so the six engine caches inside
it were covered on the day §4 shipped. They came to about 15 MB here, against 2.3 GB for the rows above.

Four things the work settled that the research did not anticipate:

- **Two tiers means two providers, and there is no third option.** A plan carries its *provider's*
  tier, so a Tier 3 child declared inside a Tier 1 provider is pre-selected and removed under a
  sentence promising nothing is lost. The proposal put `logs` and `Crashpad` in the same scope as the
  caches; they are a separate provider, on the precedent §6's crash-dump and servicing-log providers
  set.
- **Identification needs a second marker, and `Local State` is not it.** §4's marker says the folder
  belongs to *an Electron application*, which is exactly the identification that must not be enough
  here: `CachedData` and `logs` are names anything may carry. A folder qualifies only if it also
  holds `User\globalStorage\state.vscdb`, which the editor's own storage service creates on first
  run. Requiring both means the three providers over this one folder agree about which folders they
  may enter.
- **The `<commit>` layout could not be used, and the proposal assumed it could.** "Every folder
  except the installed build's commit" needs to know which commit is installed, and nothing in the
  user-data folder records it — searching the whole folder for each of the sixteen names found each
  only under `CachedData` itself. The editor's own cleaner reads `product.json` from its install
  directory, which is not discoverable from here for an arbitrary derivative, and inferring the
  install path from the folder name is the guess §5.2 refuses. So `CachedData` is targeted whole. The
  cost of including the live build's folder is one slower start, which is what Tier 1 promises, and
  §4 already offers the identical artefact whole under Chromium's own name.
- **One folder can have three owners, and §7.1 could only ask one of them.**
  `ExploreActionPolicy` resolved a path to the *innermost* tool root and then asked that one root
  alone. With `ChromiumCacheProvider`, `VsCodeCacheProvider` and `VsCodeLogProvider` all declaring
  the same user-data folder, whichever was constructed first answered for the whole of it, and every
  child the other two recognise was refused — silently, and reversibly by reordering a list nobody
  would think to look at. It now asks every declaration at that depth and allows a child one of them
  recognises. The Storage page was never affected: a provider consults its own table there.

Confirmed and left alone: `CachedConfigurations`, which was present on the measured machine at a
negligible size and is not documented anywhere in the editor's own source that could be found. It is
Tier 4 by construction like any unrecognised child, and adding it would be a name in an allow-list
with no reasoning behind it.

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

**The installer package caches were re-examined and the exclusion held**, which is worth recording
because one of them is the largest location on this machine that no provider reaches.
`C:\ProgramData\Microsoft\VisualStudio\Packages` measured 7.7 GB, beside `C:\ProgramData\Package
Cache` at 6.7 GB, and §9 previously covered the pair by a plural rather than by name. Both are now
named in §4.4 and §9, both are named survivors on the one provider that declares `%PROGRAMDATA%`, and
the Explore catalogue explains each of them. What settled it against a Tier 2 provider was not the
consequence but the mechanics: the vendor's `--nocache` route frees nothing at the moment it runs, and
the folder has 1,249 children with the installer's own per-instance state among them, so §5.2 has no
allow-list to write. The reasoning is in
[cache-locations.md](../cache-locations.md#the-visual-studio-installers-package-caches--no-way-to-clear-them-safely).

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

### Squirrel's superseded application builds — Tier 2, measured at 719 MB for one application ✅ done

**A third carve-out, and it is the one place "old application builds" above turned out to be too
broad.** Squirrel is the updater a large family of Windows desktop applications ships with. It
installs each version into `%LOCALAPPDATA%\<app>\app-<version>`, launches whichever is newest, and
deletes the older ones itself — but its clean-up excludes both the build it has just installed and
the one that build replaced, so a full second copy sits on disk until the update after next.

What makes this different from an SDK is that it is not an uninstall. Nothing records the old build
as installed: the uninstaller entry names the application's folder, the shortcut runs the shim in
that folder, and the shim resolves the highest version at launch every time. The framework's own
clean-up comment calls the previous versions dead — "already uninstalled, but not deleted" — and
its documentation states plainly that rolling back to one is not supported.

Two things were established from the framework's source before anything was offered, and both moved
the design:

- **The retention is deliberate rather than a failed delete**, and the vendor documents it. That is
  what keeps this at Tier 2 rather than Tier 1: nothing re-creates a build, and Squirrel also runs
  the application's own `--squirrel-obsolete` hook against a version before deleting it, which
  Deguffer does not run.
- **The `packages` folder cannot be removed whole**, which is what the obvious design would have
  done. `Update.exe --processStart` reads `packages\RELEASES` with no error handling to decide which
  build to launch, and that is the shortcut style Squirrel's own install documentation gives. So the
  index decides instead: only package files it has stopped naming are offered, and only those not
  newer than the installed build, since a downloaded update is written there before the index is
  rewritten.

Shipped as two providers, because the tiers differ: the staging leftovers and the spent packages are
Tier 1, and the superseded builds are Tier 2. Reasoning and protected neighbours are in
[../cache-locations.md](../cache-locations.md).

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
| `C:\ProgramData\Microsoft\VisualStudio\Packages`, `C:\ProgramData\Package Cache` | The installer package caches, 7.7 GB and 6.7 GB. Nothing clears either on request, and the payloads cannot be told from the installer's own state by any rule §5.2 permits. Reported in Explore, never offered — see §6 |
| Chromium `Local Storage`, `IndexedDB`, `Cookies` | Sit beside the six safe cache names and hold sign-in state and offline data. Tier 3 |
| `.cargo\credentials.toml`, `.m2\settings.xml` | Registry authentication tokens and encrypted server passwords, in the root of a directory whose children are being deleted. The §5.2 case exactly |
| Steam `steamapps\downloading` | Looks temporary. Holds the in-progress half of a patch; deleting it restarts the download |
| Squirrel `packages\RELEASES` and `.betaId` | An index and an identifier, in a folder of downloaded packages. `Update.exe --processStart` reads the index with no error handling to decide which build to launch, so removing it stops the application starting from its own shortcut. Named survivors on the Squirrel provider — see §12 |
| Steam `userdata`, `steamapps\workshop`, `widevine` | Cloud saves, settings and screenshots; subscribed Workshop content; and a downloaded decryption module. All three sit beside the client's web caches, and none of them is one. Named survivors on the Steam provider |
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
| pnpm and conda ✅ | Expected one link-aware measurement to unblock both. The walk can answer and the file table cannot do it better, but only pnpm needed the answer — conda reports its own link-aware figure, so it shipped on PlatformIO's shape. §1a records the split | researched |
| Per-project build output ✅ | Expected §5.3's exclusion generalised, and that was indeed the larger half. Unity, Rust, node_modules and .venv shipped; `.vs` split out below, `dist` and Dart's `build` declined for want of evidence inside the directory — §2 records why | 5.8 GB |
| Squirrel staging and superseded builds ✅ | Expected the survey's "old application builds" to stay out of scope, and one framework's own source moved it. Two providers rather than one, because the staging and the builds land at different tiers; and the `packages` folder had to be read through the application's own index rather than removed, because a shortcut reads the index in it — §12 records both | 1.3 GB |
| Visual Studio `.vs` per solution | Split out of the row above once measured properly. Not the Tier 1 folder with one `.suo` the survey assumed: a quarter of it by size is AI chat history, file snapshots and coverage records, and it needs recognised children applied *inside* it at two nesting levels | 1.5 GB |
| MSIX redirection | A classification rule, not a provider. Changes what every other provider can see | 16.1 GB |
| Cloud sync dehydration | A third kind of `CleanupStep`, and a §5.6 negative that asserts survival rather than removal | 0.2 GB |
| Windows Search index | Service control, which Deguffer does not do today. Decide the policy before the provider | 2.2 GB |

The last two rows are different in kind from the rest. Cloud-sync dehydration and service control
are not new providers, they are new **capabilities**, and each widens what the safety model has to
reason about. The tier model handles them. The plan and execution types do not, yet.

---

## Open questions this survey raises

1. ~~**Does `LongPath` cover a hard-linked file's identity?**~~ **Answered.** The walk can measure
   this, unelevated, and the file table cannot do it better.

   `GetFileInformationByHandleEx` with `FileStandardInfo` returns a file's hard-link count, its
   allocated size and its logical size from one `FILE_READ_ATTRIBUTES` handle, and that handle is
   granted at either privilege level — observed on a real hard link on an unelevated process before
   any of `HardLinkAwareScanner` was written. The MFT record does carry the count at offset `0x12`,
   which `MftRecordHeader.Read` still does not read, and **it should stay unread**: the table is
   openable only under elevation, so a link-aware total taken from it would disagree with the
   unelevated total for the same tree, and §6.3 makes unelevated the ordinary run. One route serving
   both is worth more than a faster route serving one. The result therefore carries no
   `FallbackReason`, because elevating would change nothing and offering it would be false.

   **What the answer does not cover, and this is the part to carry forward.** The rule counts a file
   only when it has exactly one link, so a file linked twice *inside* the measured tree is dropped
   even though removing the tree would free it. That under-reports rather than over-reports, which
   is the direction §5.4 allows, and telling the case apart means enumerating every link's name per
   multi-linked file — for stores whose layout never produces it, since content-addressed files link
   outward rather than to each other. The figure is also a prediction rather than a measurement:
   link counts move whenever a project installs or removes a dependency, so every result is marked
   approximate however exactly its bytes were read.

   **And it was only ever pnpm's question.** conda's own clean skips any file with more than one
   link, so its dry run already reports a link-aware figure computed by the tool that owns the
   records. §1a records why that made this two phases rather than one.

2. ~~**How is a live source tree detected?**~~ **Answered.** Three signals, all of them positive
   evidence, all readable without elevation, and none of them a process name.

   **The Restart Manager answers for a *file*.** `RmStartSession` / `RmRegisterResources` /
   `RmGetList` will name the processes holding a path open, and it does so unelevated — established
   on this machine, against a file held open by another process, before any of `LiveTreeInspector`
   was written. **It refuses a directory**, returning `ERROR_ACCESS_DENIED`, so there is no way to
   ask "is anything under this tree open" and no substitute for asking about a named file. That makes
   it §5.1-shaped: the provider declares the file its tool locks, and Unity's `UnityLockfile` is the
   case. Whether that file *exists* is the weaker test, because a crashed editor leaves one behind
   for ever.

   **The process table answers for a *directory*, twice.** A running program whose executable sits
   inside the directory is an activated `.venv` or a binary started from `target\debug`. A running
   program whose working directory sits inside the *project* is a build in flight, a shell, or an
   editor with the solution open. One pass over ~500 processes costs about 30 ms and needs no
   elevation. The second of those is what makes a name check unnecessary, and it is worth recording
   why the name check had to go: a live Visual Studio solution's `.vs` index is held open by
   `DevHub.exe`, a service host, while `devenv` holds nothing in it — observed, not reasoned about.

   **What the answer does not cover, and this is the part to carry forward.** A process holding a
   file deep inside a tree that is neither its own image nor its working directory is invisible to
   all three, and nothing unelevated answers that at directory granularity without enumerating every
   handle on the machine. So the veto can miss and must never fire wrongly, which is why every signal
   is positive evidence and why `LiveTreeFindings.Complete` keeps "could not tell" distinct from
   "nothing is using it". §7's age column carries the rest of the decision: the veto answers *now*,
   and age answers *dormant*.

   **One cost worth naming.** The working directory has no documented accessor, so it is read out of
   the process environment block at offsets Windows does not promise to keep. A layout that moved
   would produce nonsense matching nothing, which reads as "dormant" — the dangerous direction. The
   offsets are therefore checked against Deguffer's own process, whose working directory is already
   known, and a mismatch turns the mechanism off and reports incomplete rather than empty.
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
