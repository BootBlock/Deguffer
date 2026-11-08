# Scan speed — audited backlog

> **Status:** 🟢 ACTIVE — an audit of where a Clean preview and an Explore scan spend their time,
> verified against the code and filed as #441 to #492, and a second audit of the clean, the
> duplicate search, start-up and file I/O, filed as #494 to #537. Work each in its own order below:
> the measurements in each stage 0 decide the larger items after them. Flip to ✅ COMPLETE and
> `git mv` into `done/` when every issue below is closed or rejected, or supersede it with a newer
> plan.

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
| #477 | The experiment that separates the two causes of the half-speed raced walk (`after-the-scanner.md` item 7). Done: neither cause holds warm; a read through the volume's handle does (#544). |
| #479 | Timing of each stage of an index build, to explain the system volume's slow read per record. |
| #543 | Timing of the steps of opening a volume's table, which #479 found costs the system volume 0.3 to 1.5 s. Done: the cost is an NTFS flush of the volume before a handle's first read past the boot file, which neither a narrower handle nor extended DASD I/O skips (#545). |
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
| #470 | Volumes, and fallback walks, measured one after another. Done: a measurement runs one pipeline for each set of physical disks at the same time, the volumes of one set in turn, and the walking scanners run the walks of separate disks at the same time. Where any disk is not known, everything stays in turn. |
| #471 | Approved source folders searched one after another. |
| #469 | The hard-link-aware scan builds two paths and a by-name read per file. Done: each file is read by its bare name relative to a handle the walk holds on its folder while the callback runs. On one 33,115-file pnpm store the links route allocated 9.4 to 14.0 MiB rather than 31.2 MiB, and its time did not move outside the noise. |
| #468 | Open each directory relative to its parent. Measured, not done: a prototype gained nothing beyond the noise, warm, at 1 and 16 threads, on three real trees and one made 40 folders deep, and used more processor time in 9 of 12 cases. `DirectoryQuery` records the figures. Its safety gain, a junction put in place of an already-listed folder, is #551. `safety`. |
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
| #478 | Low I/O priority for a raced build. #477 found the disk queue a cause only cold, for a build of another volume on the walk's disk. Done: a build reads at low priority only while a walk on one of its disks is in flight, because one read low throughout took 2 to 11 times as long with nothing racing it. Cold, a walk beside another volume's build went from 2.68 and 3.03 times as long as alone to 0.99 and 1.03 (`after-the-scanner.md` item 7). |
| #481 | One subtree traversal for every age guard. |
| #483 | A string decoded for each hard link's name and almost never kept. |
| #544 | A read through a volume's handle slows the next walk of that volume without reading the drive: the cause #477 found. Done: NTFS flushes the volume at a handle's first read past 8 KiB, which no other way of reading the table avoids, so the build now makes that read beside the raced walk rather than before it (`after-the-scanner.md` item 7). The first walk of the volume after the flush still waits on a checkpoint of its log. |
| #545 | A busy volume's open waits 0.3 to 1.5 s on an NTFS flush on the thread that asked for its index, so the raced walk starts late. Move record 0's read to the build's thread. Done with #544: the open reads only the boot sector, and the build finds the table. |
| #482 | Directory names in a name pool. #477 found collections are not the cause, so this cuts allocation only. |

### 7. Memory churn in providers

| Issue | Note |
| --- | --- |
| #460 | Every bounded file read allocates its whole limit, 16 MB for each solution file. |
| #461 | A whole restore manifest parsed to read two values. |
| #462 | Candidate folders recognised one at a time. Done: every source-tree row and Explore's declaration recognise their candidates through `DiskBoundWork`, several at once up to the walk tuning of each volume and inside the shared listing budget, answered in the search's order. The Git LFS, Nx and `.vs` rows are covered too. Not timed. |
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
| #474 | Never enter `.hg`, `.svn`, `.bzr` or `__pycache__` in source discovery. Done: those four, `.jj` and `_darcs` join `.git` and `node_modules` in `SourceTreeBoundary`, and §5.2 states the rule a name must meet to join. |
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

## Second audit: the clean, duplicates, start-up and file I/O

A second audit read what the first did not: the clean itself (`Execution`), the safety probes and
inspectors (`Safety`), the duplicate search, the stores and the start-up path, the App, every
provider not named above, and the file I/O primitives across the codebase. It was checked against
every open and closed issue, including #441 to #492, and filed as #494 to #537. Some costs were
timed with throwaway programs against temporary files, outside Deguffer: a Restart Manager session,
`GetVolumePathNameW`, the final-path query, a store's parse and write, and buffered against
unbuffered reads. Each issue says which of its figures were timed that way, and no change was
measured inside Deguffer.

### What it found, in one paragraph

**Per-entry I/O is already lean; the time goes to questions asked again and to work done one item
at a time.** A removal costs one delete per file and one per folder. What a clean and a preview wait
on is a Restart Manager session started per candidate (about 20-45 ms each, whatever it holds), a
mount-point lookup per path (up to about a millisecond), use checks that rebuild the same question
for every step, a folder pass that waits for every file in the tree, and serial loops over files
that do not depend on each other. The duplicate search asks Windows for a path it never uses three
times per file, and identifies its candidates on one thread. Start-up parses its stores through
reflection on the UI thread.

### Order of work

#### 0. Measure first

| Issue | What it adds |
| --- | --- |
| #516 | Counts and times of the volume, Restart Manager and final-path calls in a planning pass. |
| #502 | A benchmark route that times a removal pass by pass, and a clean of many small trees. |
| #503 | How long each step of a clean took, and where a removal spent it. |
| #511 | A benchmark route that times each stage of a duplicate search, per disk. |
| #525 | UI-thread milestones: time to first frame, to first map, and to apply a snapshot or a group. |

#### 1. The checks before a deletion

Each keeps what it proves and proves it more cheaply. All are `safety`.

| Issue | Note |
| --- | --- |
| #514 | One Restart Manager session for all candidates, and one per candidate only where something holds a file. The largest single gain. |
| #512 | The mount point under a path, from its folders' attributes where no level is a link. |
| #498 | The `obj` use check lists folders and parses solutions again for every step. |
| #515 | Every candidate matched against every process, with lookups repeated per pair. |

#### 2. The clean

| Issue | Note |
| --- | --- |
| #495 | Remove each folder once its own files and child folders are done, not after the whole tree. `safety`. |
| #497 | A whole-or-nothing step is walked to look and walked again to remove. `safety`. |
| #500 | Releasing local copies, and proving them standing, one file at a time. `safety`. |
| #527, #528 | The Recycle Bin: its room asked per item, and its folder walked twice on one thread. `safety`. |
| #499 | After a clean, the UI thread follows every row's locations and compares every pair. |
| #501 | Delete each file relative to its folder's handle. Measure with #502 first. Related to #468. |

#### 3. The duplicate search

| Issue | Note |
| --- | --- |
| #504 | Three final-path queries per file read, never used. Measured at about half of a describe. |
| #505 | Candidates identified one at a time on one thread. Measured at 52-63 µs a file on one thread. |
| #508 | The first stage uses the chosen checksum where XXH128 gives the same order. |
| #507 | One dispatcher post per confirmed group, each copy judged three or four times there. |
| #539 | Found measuring #507: each copy judged asked where its volume is mounted. 20,000 groups confirmed in 62-68 s before, 10-12 s after. |
| #537 | The result waits for the whole checksum store to be sorted and written. |
| #510 | Measure a solid-state disk's reader bound for the first stage, which waits on opens. |

#### 4. Providers

| Issue | Note |
| --- | --- |
| #526 | Every dated target listed once more for its age, one target after another. |
| #532 | The Visual Studio row lists each `.vs` cache up to four times. `safety`. |
| #533 | The symbol-cache rows list each entry three times before measuring it. `safety`. |
| #534 | Steam looks for every game's manifest in every library with a probe each, in two rows. |
| #535 | Both Yarn rows ask Yarn's version, and two independent queries run in turn. |
| #536 | Mod Organizer `.meta` files read one at a time, after the measurement. |

#### 5. Start-up and the App

| Issue | Note |
| --- | --- |
| #518 | Seven stores parsed by reflection on the UI thread before the first frame. |
| #517 | Every tick on the Storage page rewrites the whole selection memory on the UI thread. |
| #519 | Every Startup reading parses every package's whole manifest. |
| #520 | Registry backups read and parsed on the UI thread, and never pruned. |
| #521 | Explore rebuilds the hidden folder list on every snapshot. |
| #522 | A rebuilt folder list is filled one insertion at a time. Measure with #525 first. |
| #523, #524 | The map's shaders compiled, and a tool window rebuilt, before the first frame. |

### Waiting on a decision

| Issue | The decision |
| --- | --- |
| #494 | Run a row's steps several at a time. `RunResidue` is recorded as not safe for concurrent use. |
| #496 | Delete while the removal walk is still gathering, against the recorded walk-first design. |
| #531 | Share Claude Code's session reading for the `ReadingLimit`, against its recorded "read afresh". |
| #513 | Keep a drive's mount points for an operation, where `IVolumeInventory` documents a live answer. |
| #506 | Let each disk read files in full as soon as it has finished the first stage. Progress is recorded stage by stage. |
| #509 | Read a spinning disk's files in disk order, against "worth most first". Relates to #256. |
| #529 | Take a target's age from the measurement that walks it. Needs a scanner seam, as #465 does. |
| #530 | Move a removal's items to the Recycle Bin in one shell operation. Holding many copies open at once is the decision. |

### Checked and not filed

| Finding | Why no issue |
| --- | --- |
| Unbuffered reads for the duplicate search | Timed on a cold 1 GiB file: buffered 1 MiB reads at 1.54 GB/s against 1.05-1.29 GB/s unbuffered. |
| Size grouping, partial hashes, XXH128, a checksum cache, one reader per spinning disk | Already done (#297, #369). A file with several hard links is already read once. |
| Compare a pair of copies by bytes rather than hashes | The same bytes are read, and the checksum cache would be lost. Removal already compares bytes. |
| A process-table read per deletion step | Fixed by #163. |
| A walk per path in the after-command reading | Fixed by #383. |
| Probing each protected path before and after a run | That is the §5.6 evidence itself. |
| The refusal record and the scan estimates written or read at close | A few writes per clean, or one parse after the window has gone. |
| The sibling check's serial listings | Small beside the per-copy hold and comparison of the removals it guards. |
| The placeholder walk's open per placeholder | #355. The listing's attributes cannot show a partly downloaded file's local bytes. |
| Memory's refresh, the installed-app keys, scan and size history | Off the UI thread, bounded, or once per visit. #142 fixed the list rebuild. |
| The item guide, list virtualisation, blocking waits, ReadyToRun | A recorded choice, already virtualised, none found, and already set. |
| Storage rows recomputed per arrival | Quadratic, but over about 150 rows. |
| Header reads through a 4 KB buffer, probe then open, `FileSystemInfo` listings | Once per provider per pass and microseconds each, or filed as #463, #464 or #526. |
| Regexes, comparers and sets per call; process, volume and registry reads | None built per call; the reads are already cached between readings. |
| Provider reads (Claude Code transcripts and session folders, Conan, Arduino, Dart, Chromium roots, Maven, restore points, temporary roots, Steam, Squirrel, RetroArch and Mozilla discovery) | Once per pass on small files or few folders, already memoised, or covered by #455, #460, #461 or #462. |
| The Visual Studio and Nx rows recognise candidates one at a time | The same loop as #462, and fixed with it. |
| Two process listings, and four small `Safety` micro-costs | Milliseconds per pass, or the cheaper form would not prove the same thing. |
