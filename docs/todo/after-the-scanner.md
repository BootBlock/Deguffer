# After the scanner — sequenced backlog

> **Status:** 🟢 ACTIVE — the agreed order of work following the §5.5 scanner. Items 0 and 1 are
> done; 2 onwards are not started. Flip to ✅ COMPLETE and `git mv` into `done/` when the list is
> exhausted, or supersede it with a newer plan.

The §5.5 scanner (MFT fast path, observable fallback, cross-run size cache, progressive preview)
landed on `feature/mft-scanner`. This is what follows, in the order it should be done and with the
reasoning that produced that order, so a later reader can disagree with the reasoning rather than
guess at it.

---

## 0. Blocking — verify the elevated MFT read ✅ done

**Outcome: it passed.** The volume handle opened, the geometry parsed, and the table was fully
readable rather than refused; the index built over roughly 2.4M records in about five seconds and
every provider resolved through the fast path with no fallback note and no approximate size. The
reasoning below stands as the record of why the branch was held.

**The fast path has never been executed.** Reading the MFT needs administrator rights, so every
test covers it through a synthesised table and the `IMftSource` seam; the only thing exercised
against a live volume is the *unelevated* refusal, which correctly reports `NotElevated`.

Until someone runs Deguffer elevated and observes an index actually build, the fast path is
unproven. It fails closed — every failure route falls back to the bounded walk, which is fully
verified — so this is not a correctness risk to users. It is a "the feature may simply not work"
risk.

Everything below assumes this passed. If it did not, fixing it supersedes all of it.

---

## 1. Make the fast path reachable ✅ done

**Outcome:** the preview offers "Elevate and rescan" when — and only when — a scan fell back for
want of rights. The relaunch carries a switch that re-previews on launch, so the button's promise to
rescan is kept rather than leaving the user on an empty window. The decision itself lives in
`ElevationOffer` in Core so it is provable without a WinUI host; the plan now carries its
`FallbackReason` as data, because matching on the display sentence to decide this is how a reworded
string silently disables the offer.

**The scanner is unreachable on a default run.** §6.3 has the app running unelevated, so
`VolumeMftSource.TryOpen` returns `NotElevated` and every user takes the slow walk. The UI now
*diagnoses* this precisely — "Run Deguffer as administrator to read the file table directly" — and
offers no way to act on it.

That is a half-delivered feature: the diagnosis shipped, the remedy did not. Without it the MFT
reader only ever fires for someone who happens to right-click the executable.

- An "Elevate and rescan" affordance on the preview, turning the fallback note from a sentence into
  a button.
- Relaunch unpackaged via `ShellExecute` with `runas`, and carry elevation state on the view-model.
- §6.3 already sets the policy — *"request elevation only for the fast scanner and for
  `C:\Windows\Temp`"* — so only the mechanism is missing.

## 2. Two more Tier 1 providers ✅ done

**Outcome:** both landed, and §6.2's "one class plus tests" held — neither needed a change to
`IDirectoryScanner`, `CleanupProviderBase`, or any safety type. The seam is right; the fourth and
fifth providers cost one file each plus a line in `CleanupPlanner.CreateDefault`.

Two things the audit's framing got wrong, both found before writing code:

- **uv's target is not `%LOCALAPPDATA%\uv`.** That is uv's whole state directory: `tools` (CLI
  tools installed with `uv tool install`) and `python` (managed interpreters) sit beside `cache`
  under it. Deleting the root to reclaim the cache would uninstall them, so uv is a §5.1
  command-based provider — `uv cache dir` to locate, `uv cache clean` to evict — with those
  siblings named as protected paths. `--force` is deliberately not passed: it tells uv to ignore
  its own in-use checks, and §5.3 prefers warning over overriding.
- **cpptools reclaims `ipch` only.** The rest of the directory is one hex-named database directory
  per workspace ever opened, and a hex name carries nothing that can be checked, so every one of
  them is Tier 4 by construction. That leaves roughly 1.8 GB of the 6 GB unreachable — see item 5.

`vscode-cpptools` exercised §5.2's `DisposableChildSet` as expected. Observed on the audited
machine: 4.3 GB targeted for cpptools, 3.6 GB for uv.

### The original reasoning

Both from §4.2, both Tier 1, neither needing new safety machinery:

| Source | Observed | Notes |
| --- | --- | --- |
| `%LOCALAPPDATA%\Microsoft\vscode-cpptools` | 6.0 GB | C++ IntelliSense databases; rebuilt on next open |
| `%LOCALAPPDATA%\uv` | 3.5 GB | Python (uv) package cache |

`vscode-cpptools` is path-based with no eviction command, so it exercises §5.2's
`DisposableChildSet` the way Gradle does rather than §5.1's command path.

Beyond the reclaim, these are a check on the scanner seam: §6.2 promises a new cache is "one class
plus tests". If that turns out not to hold for the fourth and fifth providers, the
`IDirectoryScanner` seam is wrong, and it is much cheaper to learn that now than after the seam has
five more callers.

## 3. The §7 confirmation flow — the real fork 🟡 partly done

**Outcome: the seam and Tier 2 landed; Tier 3 and the age column did not, on evidence.**

`ConfirmationRequirement` decides from a plan's tier what must be satisfied before it runs — nothing
for Tier 1, an acknowledgement for Tier 2, the typed phrase for Tier 3, and for Tier 4 an answer that
does not exist. It sits in Core for the reason `ElevationOffer` does: the rule is then provable
without a WinUI host. The planner derives the requirement itself rather than trusting that the caller
asked, so a shell that forgets fails closed.

PlatformIO is the first Tier 2 provider. It was chosen over Android on evidence rather than size: it
is the only one of the two with a documented eviction command, so §5.1's preferred path exists at
all, and its worst outcome is a re-download rather than a destroyed signing key.

**Tier 3 was not attempted, because §8 question 2 resolved badly — see item 5.** The age column was
not built either: §7 scopes it to per-workspace and per-project data, which is precisely the blocked
Tier 3 subject. Today's providers are whole-cache, one row each, with nothing per-item to date, so
building the column now would be a first-class column with nothing to put in it. Both remain open.

### The original reasoning

`CleanupPlanner.ExecuteAsync` throws `NotSupportedException` for anything above Tier 1, deliberately,
so that the first Tier 2/3 provider cannot silently inherit a deletion path that skips the extra
confirmation §7 requires.

Every remaining high-value source sits behind that guard:

| Source | Observed | Tier |
| --- | --- | --- |
| `%APPDATA%\Code\User\workspaceStorage` | 11.3 GB | 3 |
| `%LOCALAPPDATA%\Android` | 6.7 GB | 2 |
| `%USERPROFILE%\.platformio` | 5.7 GB | 2 |

That is 23.7 GB — slightly more than the entire Tier 1 haul the founding audit achieved. Unblocking
it needs typed confirmation for Tier 3 and the age column §7 calls a first-class column, which is a
meaningfully larger piece of UI than anything the shell does today.

**Two constraints to hold when this is picked up:**

- **`workspaceStorage` must not be the first Tier 3 provider**, despite being the largest single
  number in the audit. §8's open question 2 — whether the `workspace.json` hash-to-path mapping is
  stable across editor versions — is unresolved, and the whole per-workspace UI depends on it.
  Answer that before writing code. Android or PlatformIO are the safer first Tier 2 subjects.
- **§3 exists because of this directory.** It looks like cache, sits in a path that suggests cache,
  and is mostly AI chat history. Nothing about its size or shape distinguishes it. Treat the tier
  model as the product, not the sizes.

## 4. Deferred, with a reason

- **§5.4's second pair** — space freed *inside* a VHDX versus on the host. Not a scanner concern:
  it cannot be measured from the filesystem and comes from the container tool's own accounting.
  `CleanupStep.Estimated` is already a `ScanSize` rather than a scalar, so it has a home when a
  Docker provider needs one.
- **USN journal for cache invalidation.** Considered and rejected for now. It shares the MFT's
  elevation requirement, so it would only work where the fast path already works — leaving the
  fallback path with a second, different invalidation strategy. It also carries no sizes: a record
  says a file changed, not by how much, so it is an optimisation layered on the MFT reader rather
  than an independent mechanism. `ScanEstimateCache` is display-only and expiry-based, which is
  sound precisely because a remembered size is never returned as an answer.
- **§8 question 1 — detecting an idle toolchain.** Android and PlatformIO are 12 GB combined and
  pure waste *if* idle, and NTFS last-access times are unreliable by default. Worth revisiting now
  the index exists: `$STANDARD_INFORMATION` timestamps come back in the same MFT pass already being
  made, which may be a better signal than a directory walk could afford.
- **§8 question 4 — undo.** Still likely impossible at these sizes. If so, §7 should say so plainly
  rather than implying reversibility.

## 5. Raised while doing items 2 and 3, not yet decided

### From item 3

- **§8 question 2 is answered, and the answer is "not safely".** The `workspace.json` schema is
  stable enough — `folder`, then `workspace`, with a pre-2019 `configuration` spelling that may still
  be on disk — but the *mapping* is not one-to-one and is not a live fact. For a local single-folder
  workspace the id is `md5(fsPath + birth-time)`, not a function of the path, so deleting and
  recreating a folder, restoring it from backup or re-cloning it mints a second storage directory
  with an identical `workspace.json`; a machine surveyed while answering this already had two paths
  owning two directories each. Several child kinds carry no `workspace.json` at all (`ext-dev`,
  `empty-window`, timestamp-named ids, and any folder whose un-awaited metadata write lost a race),
  and one 8-hex-character child was found that no derivation in current editor source accounts for.
  A per-workspace UI is therefore only safe if the mapping is treated as a best-effort *label* on a
  storage folder rather than an identity: sizes summed over a group, deletion targeting the group,
  absence of metadata classified Tier 4, and a missing target path never taken as licence to delete.
  That is a materially different feature from the one item 3 assumed, which is why it was not built.
- **The audit's headline sizes are directory totals, not reclaimable amounts.** PlatformIO's 5.7 GB
  is its whole core directory; the disposable cache within it measured ~72 MB on the audited machine,
  and the tool's own `prune --dry-run` agreed to within a rounding error. `%LOCALAPPDATA%\Android`'s
  6.7 GB is almost entirely installed SDK components, and its only true cache — `.android\cache` —
  is around 3 MB. The "23.7 GB blocked" figure should not be read as 23.7 GB of reclaim; nearly all
  of what remains is `workspaceStorage`, which is the subject the point above just made hard.
- **Any future Android provider must never allow-list `cache` alongside its siblings.** The ~3 MB
  `.android\cache` sits directly beside `debug.keystore`, `adbkey`/`adbkey.pub` and `avd`. Losing the
  keystore changes the debug signing identity and invalidates every API key registered against its
  fingerprint; losing `adbkey` revokes every device's trust; an AVD holds user data that cannot be
  re-downloaded at any price. A one-character slip in a name comparison there costs more than the
  entire Android reclaim is worth, so the conservative shape is a standalone provider scoped to that
  one path which never enumerates `.android` at all. `%LOCALAPPDATA%\Android`'s own regenerable
  children (`.temp`, `.downloadIntermediates`) were both empty at rest.
- **The §6.3 long-path caveat below was confirmed, not merely suspected.** `LongPathsEnabled` is set
  on the machine that verified item 3, so the new long-path test in `PlatformIoCacheProviderTests`
  passes without proving anything there. It is written to fail on a machine without the key.

### From item 2

- **The cpptools workspace databases (~1.8 GB) are unreachable by name.** Recognising them needs
  classification by *content* — a child holding nothing but `*.BROWSE.VC.DB*` — which is a genuine
  extension to the §5.2 model rather than a wider name list, and so wants deciding rather than
  assuming. The conservative reading is that it is a stronger check than a name match, not a weaker
  one: it verifies what a directory *is* instead of trusting what it is called.
- **`InvalidateCaches` does not clear memoised tool answers in `NpmCacheProvider` or
  `NuGetCacheProvider`.** `UvCacheProvider` overrides it to drop its resolved cache directory,
  because `UV_CACHE_DIR` can move between scans; the other two memoise the same way and keep a
  stale answer across a rescan. Same three-line fix in each, not made here to keep this change to
  its subject.
- **The §6.3 long-path tests do not discriminate on a machine with `LongPathsEnabled` set.**
  Established by removing `LongPath.Extended` from `DirectoryRemover` and watching the suite stay
  green: .NET accepts >260-character paths without the `\\?\` prefix when that registry key is on,
  so these tests can only fail on a machine without it. This affects the existing long-path tests
  as much as the new one. Worth forcing the test process to opt out so the assertions have teeth
  everywhere.
