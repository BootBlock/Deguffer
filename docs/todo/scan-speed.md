# Scan speed — audited backlog

> **Status:** 🟢 ACTIVE — an audit of where a Clean preview and an Explore scan spend their time,
> verified against the code and filed as #441 to #492. Work it in the order below: the measurements
> in stage 0 decide stages 2, 4 and 6. Flip to ✅ COMPLETE and `git mv` into `done/` when every
> issue below is closed or rejected, or supersede it with a newer plan.

This audit asked whether a preview and an Explore scan can finish sooner, especially on solid state.
Every finding was checked against the code at the time of writing and against the closed
`type: performance` issues (#342 to #390 already removed most of the per-entry cost of the walk).
Each surviving finding is an issue with its mechanism, its line references, an estimate and the
test a fix needs. Nothing here was measured: every gain is an estimate, and each issue says so.

## What the audit found, in one paragraph

**The disk is rarely what a pass waits on.** The repository's own figures say the walk is bound by
the processor in the kernel (5.9 s cold against 5.5 s warm on the source volume, level from 12 to
64 threads), and the table read is tuned to the drive (#249). What a pass waits on is the order the
planner starts providers in, tools asked one question at a time, a lock that wakes every waiting
walk, and work that runs before a result is shown. The largest single item is that an elevated pass
starts DISM's analysis, which takes a minute or more, only when it reaches the 110th provider.

## Order of work

### 0. Measure first

These decide whether the larger items in stages 2, 4 and 6 pay, and they are the evidence G8 asks
each of them for.

| Issue | What it adds |
| --- | --- |
| #443 | Per-provider planning time, and a benchmark route that times a whole planning pass. |
| #476 | A benchmark route that runs several walks of one volume at once. |
| #477 | The experiment that separates the two causes of the half-speed raced walk (`after-the-scanner.md` item 7). |
| #479 | Timing of each stage of an index build, to explain the system volume's slow read per record. |
| #466 | A `FakeProcessRunner` that can prove queries overlap. Stage 3 needs it for every test. |

### 1. Correctness

| Issue | Why it is first |
| --- | --- |
| #472 | A file whose timestamp is past the year 9999 throws out of the walk, the removal walk included. NTFS accepts such a timestamp from any program. |

### 2. Pass scheduling

| Issue | Note |
| --- | --- |
| #441 | Start DISM's component-store analysis when an elevated pass starts. The largest single gain. |
| #442 | Hand out planning slots longest first, by remembered cost from #443. |
| #450 | Let a provider start the tool query its plan will wait on when the pass begins. |
| #451 | Show each location's check time. Built on #443. |

### 3. Tool queries that wait on each other

Each needs #466 for its test.

| Issue | Provider or seam |
| --- | --- |
| #452, #449 | Composer: seven queries in a row, and no stop after a failure. |
| #453, #449 | Arduino: up to sixteen queries in a row. |
| #454 | conda: dry run after `info`. |
| #456 | Poetry: three starts in a row. |
| #457 | PlatformIO: prune dry run after `system info`. This row is often the end of a pass. |
| #458 | Git LFS: three runs per store, store by store. |
| #444 | The git tracked-file check: one repository at a time, four callers. |
| #445 | Tool output read on thread-pool threads. |
| #448 | PATH probing for each tool lookup. |

### 4. Walk contention

#467 goes first: #470 and #471 run more walks at once, which makes its wake-up cost worse.

| Issue | Note |
| --- | --- |
| #467 | Every listing that ends wakes every waiting walk on its volume. |
| #470 | Volumes, and fallback walks, measured one after another. |
| #471 | Approved source folders searched one after another. |
| #469 | The hard-link-aware scan builds two paths and a by-name read per file. |
| #468 | Open each directory relative to its parent. Measure with #476 first. `safety`. |
| #473 | A frame array per child directory. |

### 5. Explore: time to the first map

| Issue | Note |
| --- | --- |
| #485 | The finished map waits on a full volume read and the shadow copy service. |
| #490 | The first walk snapshot waits 750 ms. |
| #486 | One lock per parsed record around the name pool. Measure first. |
| #488, #480 | Tree assembly copies every column twice, and the links are sorted through LINQ. |
| #489 | A string per listed name, interned under the builder's lock. |

### 6. The file-table route

| Issue | Note |
| --- | --- |
| #478 | Low I/O priority for a raced build. Only if #477 shows the disk queue is the cause. |
| #481 | One subtree traversal for every age guard. |
| #483 | A string decoded for each hard link's name and almost never kept. |
| #482 | Directory names in a name pool. Helps #477 only if collections are the cause. |

### 7. Memory churn in providers

| Issue | Note |
| --- | --- |
| #460 | Every bounded file read allocates its whole limit, 16 MB for each solution file. |
| #461 | A whole restore manifest parsed to read two values. |
| #462 | Candidate folders recognised one at a time. |
| #463 | Each temporary-folder row lists the same folders again. |
| #464 | A folder's attributes read twice, for presence and for a link. |

## Waiting on a decision

Each of these changes a recorded design choice or what Deguffer finds, so each is
`status: needs-design`.

| Issue | The decision |
| --- | --- |
| #447 | Keep a tool's answer about its cache location across passes. G4 says "for the operation's life". |
| #446 | Start npm's own script directly, skipping `npm.cmd`, which may run a different npm from the user's shell (§5.1). |
| #455 | Ask Conan for its store and its home at once, against the documented order. |
| #459 | Ask the driver-package cleanup at the same time as pnputil, against the order recorded as deliberate. |
| #465 | One walk of a replaced JetBrains version for its age and its size. Needs a scanner seam. |
| #474 | Never enter `.hg`, `.svn`, `.bzr` or `__pycache__` in source discovery. |
| #475 | Stream the bytes counted so far into the Clean preview (`scan-progress.md` route B). |
| #484 | Start a volume's index build when the pass starts (`after-the-scanner.md` item 7's G1 question). |
| #487 | Race the walk against the table for an Explore scan of a folder. |
| #491 | Draw the last kept scan of a drive while a new scan runs. |
| #492 | Draw snapshots during the file-table read. |

## Checked and not filed

| Finding | Why no issue |
| --- | --- |
| ReadyToRun for the first index build | Every release publish profile already sets `PublishReadyToRun`. The slow first run was the benchmark's, which is not published that way. |
| One table read shared by Explore and the planner | Decided by #254 (`after-the-scanner.md` item 9) against #172. |
| PlatformIO and Delivery Optimization start last | The same cause and fix as #442. |
| Hugging Face asks `env` before `cache ls` | `cache ls` needs the hub folder `env` reports. |
| `ChromiumUserDataDiscovery` blocks on a task | The task is complete in a preview, and #386 designed the wait in Explore. |
| Shared counters touched per directory | About a tenth as often as the per-file writes #371 removed with no measurable gain. |
| Walk helpers end and restart | Since #354 a restart reuses a parked thread. Keeping helpers alive needs a G3 knob. |
| Resolving a scoped folder passes over the table per component | About 10-30 ms against a 3-5 s read, and `MftExploreReader.FindChild` records the trade. |
