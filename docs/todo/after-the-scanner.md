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

## 2. Two more Tier 1 providers

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

## 3. The §7 confirmation flow — the real fork

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
