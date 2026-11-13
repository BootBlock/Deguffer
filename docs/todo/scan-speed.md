# Scan speed — audited backlog

> **Status:** 🟢 ACTIVE — an audit of where a Clean preview and an Explore scan spend their time,
> verified against the code and filed as #441 to #492, and a second audit of the clean, the
> duplicate search, start-up and file I/O, filed as #494 to #537, and a third audit of the map's
> drawing, Explore's lists and removal, the duplicate search's memory and the shell's lists, filed as
> #676 to #690, and a fourth audit of the running App, real tools, real drives and elevated passes,
> filed as #691 to #710 and #737 to #743, and a fifth audit of every hot path again, filed as #1014
> to #1046. Work each in its own order below:
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
| #443 | Per-provider planning time, and a benchmark route that times a whole planning pass. Done: a pass reports each provider's start and end through `timed`, `PlanTimeHistory` keeps the median of each provider's last five full passes at each elevation, and `Deguffer.Benchmark plan` prints the whole pass and each provider's start, end and time. |
| #476 | A benchmark route that runs several walks of one volume at once. |
| #477 | The experiment that separates the two causes of the half-speed raced walk (`after-the-scanner.md` item 7). Done: neither cause holds warm; a read through the volume's handle does (#544). |
| #479 | Timing of each stage of an index build, to explain the system volume's slow read per record. |
| #543 | Timing of the steps of opening a volume's table, which #479 found costs the system volume 0.3 to 1.5 s. Done: the cost is an NTFS flush of the volume before a handle's first read past the boot file, which neither a narrower handle nor extended DASD I/O skips (#545). |
| #466 | A `FakeProcessRunner` that can prove queries overlap. Stage 3 needs it for every test. |

### 1. Correctness

| Issue | Why it is first |
| --- | --- |
| #472 | A file whose timestamp is past the year 9999 throws out of the walk, the removal walk included. NTFS accepts such a timestamp from any program. Done: the walk keeps each entry's and each root's times as the FILETIMEs Windows reported, so the measuring walk, the removal walk, Explore and the JetBrains age read such a file as newer than any window, or Explore as of unknown age, and nothing throws. |

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
| #488, #480 | Tree assembly copies every column twice, and the links are sorted through LINQ. #488 done: a read places the links in columns sized to them, and assembly compacts the records and the links in one copy. On a system volume of 2.8 million records and 69 thousand links it allocated about 120 MiB less, and placed its links in about 42 ms rather than 85 to 89. Of the assembly's other passes, the parallel sort took 380 to 500 ms and keeping the names 230 to 340 ms. The column copies took about 100 ms together, and running them on parallel threads showed no gain clear of the noise. The LINQ sort is #480. |
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
| #487 | Race the walk against the table for an Explore scan of a folder. Done: under Auto a folder is raced through `RouteRace` and a volume's root still reads the table alone. The page is told about the walk while both run, and about the table once the walk ends without listing the folder. A walk that answers first carries no note. Table keeps the old behaviour. |
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
| #498 | The `obj` use check lists folders and parses solutions again for every step. Done: every check a veto handed out shares the question built for the inspector's current reading, and a new reading builds it again. |
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

## Third audit: the map's drawing, Explore's lists and removal, and the shell's lists

A third audit read, in six parallel lanes, what the first two touched least: the map's per-frame and
per-pointer work (`Exploring/Rendering`, `Exploring/Layout`, the App's map controls), Explore's data
and its list and removal path, the per-entry primitives again, the Clean pipeline and the providers
again, the shell outside Explore, and the Core subsystems outside Clean and Explore. Each finding was
checked against every open and closed issue, against both earlier audits' tables, and against the
code. Most figures were timed with throwaway programs outside Deguffer, against copies of the code or
the Release `Deguffer.Core.dll`, on a synthetic tree of a million nodes or a real WinUI 3 window, on
a shared machine. Each issue says which of its figures were timed and which are estimates. No change
was measured inside Deguffer.

### What it found, in one paragraph

**The per-entry paths are lean. The costs left are per frame, per hover and per selection.** While
the map moves, each frame that wants tiles starts its tile workers' threads again on the UI thread,
and each hover passes over every rectangle of the drawing. Each drawing gives a hue to every child of
every folder above a drawn shape, and rebuilds the names a smaller drawing removed. Explore's list
formats every row again at each snapshot, and a removal compares the selection pair by pair and asks
the policy twice on the UI thread. The duplicate search builds a list for every distinct size and a
group card builds every copy at once. The rest are small allocation wins.

### Order of work

| Issue | Note |
| --- | --- |
| #676 | Tile workers started as new threads for each frame that wants tiles. The largest gain while the map moves. Done: each worker starts once, parks between asks, and ends when the map's tiles are released. Over a 90-frame zoom of a 1,000,000-node tree outside the App, UI-thread time in `Want` fell from 18-6,059 ms to 2.4-44 ms, and only the first frame, which starts the workers, took over 0.1 ms. |
| #689 | A group of hundreds of copies stops the window for seconds. Done: each copy is a row of a grouped list, its group's heading the top of the card and its last copy the bottom, so the list realises only the copies on screen. Driven on a 500-copy group, each step timed with the automation tool's own start-up of about 0.7-1.2 s included: the search to the group's heading fell from 9.8 s to 2.4 s, a mark from 3.8-5.2 s to 1.5-1.6 s, and scrolling away and back from 3.7 s and 3.0 s to 1.4 s and 1.5 s. |
| #681, #682 | An Explore removal compares the selection pair by pair, and asks the policy twice, on the UI thread. `safety`. |
| #688 | Narrowing a plan to the ticked steps is quadratic, on the UI thread. `safety`. |
| #678 | Each hover passes over every rectangle of the drawing. |
| #677 | The branch colouring divides every child of every folder above a drawn shape. |
| #680 | The List layout formats every row again at each snapshot of a scan. |
| #679 | Map names reused by position, and rebuilt after a smaller drawing. Done: the name controls are kept up to the most a drawing has needed and the rest put away, a name of the same tree keeps the control that named its node, and the SpaceMonger lettering shares one text format per line spacing and measures a name again only when its text or spacing changes. Over 24 zoom steps of a 240-file folder in a maximised window, in the SpaceMonger lettering, sampled UI-thread time in `ExploreLabels.Show` fell from a median of 836 ms to 344 ms over three runs of each Release build, and time making name controls from 254 ms to 93 ms. |
| #683 | The duplicate search groups by size with a list per distinct size, and a 112-byte `FoundFile`. |
| #687 | The Hugging Face row lists the whole hub before and after each repository it removes. |
| #684 | Checksums held as hexadecimal strings for the session. |
| #685 | `LongPath.Resolved` rebuilds every ordinary path. |
| #686 | The hard-link-aware scan builds a string per name. |
| #690 | The list animation reads every row after an insert-only change. |

### Checked and not filed

| Finding | Why no issue |
| --- | --- |
| The map's hit test, `MapTiling.Covering`, tile layout, a full layout per drawing | 0.1-0.8 µs per hit test, 0.01-0.08 ms per covering, 0.02-0.3 ms per tile on workers, 9-34 ms per drawing on a worker. |
| Name placement and fading, `ExploreDescent.OnFrame`, `ExploreMap.Place` per frame | In compositor expressions, a few operations per frame, or writes of equal values that raise no change. |
| `ExploreLift`'s mask resized per hover, the pointer light's full clear | Plausible GPU costs that a console program cannot time. Revisit with #678. |
| Explore's carry work per snapshot, `DominantTypes`, `LargestFiles`, `SizeOf` after a removal | A few ms, off the UI thread, or 2-3 ns per call. |
| `SetThreadErrorMode` per attribute read, the MFT parse, `MftVolumeIndex.TryResolve`, `MftSubtreeTally` | Inside the noise, or tens of ms per pass at most. The rest of the parse is #482, #483 and #486. |
| `FileRemover` reading a file's attributes three times | The delete dominates: 370-612 ms against 384-819 ms for 3,000 files. |
| `Protect()` probes in series, `DriverStore.FolderOf` per package, `StepOverlap.Of`'s final paths | 20-60 ms once per pass, or under 0.3 s for 5,000 steps. #516's counts decide the last. The first estimate assumed a handful of paths per row: measured on real data it was 1.2-3.6 s, filed and fixed as #695. |
| Repeated listings in the VS Code, Steam, packaged-app, Bun and Yarn 1 rows | About 5-40 ms per pass each. |
| The Memory page's refresh and tree, `LiveList.Reorder`, marking totals, preference writes | Sub-millisecond per tick or click, or bounded lists. |
| The item-list search, one change per item | About 4 µs per change: 19-31 ms to hide 4,990 of 5,000, against a rebuild that breaks the in-place rule. |
| Startup's shortcut reads, installed apps, diagnostics, `BusyWork`, the star field | Once per reading off the UI thread, or bounded. |

## Fourth audit: the running App, real tools, real drives and elevated passes

A fourth audit measured what the first three could not time with throwaway programs: the running App
(start-up, idle use, frames while the map moves, memory over a long session), every provider read
one by one against this machine's real data, the real tool starts, real drives cold and warm, and
elevated passes. It ran in eight parallel lanes over two days on one shared machine (32 hardware
threads, NVMe solid-state disks only, an RTX 4090, one 5120x1440 display), against a Release
ReadyToRun publish of the App and harnesses that call the same Core entry points. Figures are
processor time and allocations where possible, repeated, with the spread. Each issue says which of
its figures were measured and which are estimates. Instrumentation used to measure was never
committed.

### What it found, in one paragraph

**An elevated pass holds its largest rows because of its own DISM analysis, and the rest of the cost
is in a few rows and a few App paths.** DISM's component-store analysis, started at the start of an
elevated pass since #441, makes the servicing stack create its restart-pending key, so every row
that checks for an unfinished update holds itself for the whole pass (#691). On real data the costly
rows are the ones that start PowerShell or pnputil for one number (#692, #693), that probe protected
paths one at a time or breadth-first through `WinSxS` (#694, #695), that read every Claude Code
transcript again in every pass and twice more for Explore's policy (#696, #697, #707), and that
measure Windows.old in full only to ask whether it is empty (#699). Explore's removal policy is
linear in its rules for every file a duplicate search judges (#706), and the duplicate search lists
hard-link names in series after identifying (#708). The x86 build runs out of address space (#709).
In the App, the Classic names hold most of a core while the map pans (#702), and a few UI-thread
paths do work for every tile, row or snapshot that they need only once (#703, #704, #705). Idle, the
App is not idle: once a map has been built, its camera keeps the compositor recalculating every
frame for the life of the process (#739), a covered About page flies its star field at a core
(#740), and a covered Memory page goes on reading the machine (#753). Every closed map appearance window and every Settings visit leaves its map alive (#737), a
scan leaves about 600 MB committed (#738), and Classic names draw through a second Direct3D device
(#741). Start-up spends about a third of its time before the App's own timing begins (#742, #541).

### Order of work

| Issue | Note |
| --- | --- |
| #691 | DISM's analysis creates the restart-pending key that holds the component store, driver store, Windows.old and Windows Update rows in every elevated pass. `type: bug`, `safety`. Done: a pass reads the restart keys and the servicing processes once, before it starts the analysis, and plans every row the update hold governs against that reading. The clean reads both again, and excuses only the `RebootPending` key last written during one of Deguffer's own DISM calls that began with no restart owed. On one machine, an elevated pass of the six system rows held none of them and both component store rows read the analysis. |
| #706 | The removal policy's per-file check is linear in its rules: 72 GB allocated judging one drive's duplicates. `safety`. |
| #692 | Delivery Optimization starts PowerShell in every preview for one WMI number: 1.1-1.9 s against 16-30 ms in process. Done: the figure is `MSFT_DOCurrentStatus.CacheSizeBytes` read in-process through WMI's COM interfaces (`WindowsDeliveryOptimizationStatus`), so neither a preview nor the measurements around a clean start a process, and the clear is the one PowerShell start left. On one machine, unelevated, it gave the cmdlet's own figure. The first query in a new process took 515-1,052 ms and each later one 5-54 ms, against 1,749 ms for the PowerShell route in the same session. |
| #695, #694 | `Protect` probes in series (1.2-3.6 s for a Claude Code row), and `IsPresent(WinSxS)` walks breadth-first (8.8 MB per call, 5-7 calls per pass). `safety`. #695 done: a plan's protected paths are probed as many at once as the volume's walks run, through `DiskBoundWork`, and answered in their order (`ProtectedPathCapture`), with the `protect` benchmark route to time it. #755 done: a path the row's own listing returned while it planned is recorded from that listing and asked only what the listing left open (`ListedEntries`), on the driver store, the Claude Code conversations, leftovers and rewind snapshots rows, and the rows that protect through `CacheLevelWalk`'s own overload. The `protect` route's `--listed on` halves the capture of 5,000 empty folders (median 72-75 ms against 145-192 ms). Planned on one machine, the conversations row fell from a median of 778-967 ms to 466-642 ms, the leftovers row moved within its noise, and the driver store row is pnputil's time. The records were identical, path for path, for all 10,918 paths. #694 open. |
| #707 | Explore's policy read every Claude Code transcript's head and tail on the duplicate search's critical path. Done: the declaration reads each transcript's first lines only, a title is read only for a plan and only for what it offers, and a scope that cannot reach the conversations reads none of them. |
| #696, #697 | Claude Code transcripts: read again every pass, and read 64 KiB at the tail to use about 15 KiB. #696 waits on a decision. |
| #708 | The duplicate search lists hard-link names in series after identifying: an 18-36 s gap on a source tree. Done: the names are listed in the identifying lanes as the stage's last pass, counted on the page and stoppable, and only for files still in a group. On a scratch tree of 20,000 files with 7 names each, warm, listing took 19.6 s on one thread and 4.8 s on four. The source tree itself was not measured again. |
| #709 | The x86 build is not large-address-aware: a duplicate search runs out of memory in 15 s. Done: every executable built for `win-x86` is marked large-address-aware as the build makes its apphost (`Directory.Build.targets`), and the release fails an x86 package without the flag. A search that still runs out of memory ends on the page, keeping the groups it confirmed and saying that other files may still have copies. A Release x86 publish now reads 0x0122 where it read 0x0102. |
| #737 | Every closed map appearance window and every Settings visit leaves its map alive, held by native references the collector cannot see. `type: bug`. Done: the tracker and every dispatcher timer hold their owners weakly, the window binds nothing and takes its handlers off as it closes. After 5 opens and closes of the window and 5 Settings visits, a heap dump once finalisers had run held no `MapAppearanceWindow`, `MapPreview` or `SettingsPage`, and only Explore's own `ExploreMap`. |
| #739 | After the first map, the camera's bounds and tracking keep the compositor recalculating every frame: 5.4-12.7 s of processor time a minute on an idle page, about 0.2 s without. Done: the bounds are pinned while the tracker rests ([composition-stack.md](composition-stack.md#what-landing-739-found)). |
| #740 | A covered About page holds about one core (52-62 s a minute). Done: the field pauses when every pixel of it is behind other windows, off every screen or on another virtual desktop, worked out from the windows in front of it each time a top-level window moves, shows, hides, minimises or cloaks, at most ten times a second. Activation alone was rejected, because a window beside the focused one is still seen. |
| #753 | A covered Memory page goes on reading the machine: 6.7 s of processor time a minute behind an opaque window, against 7.3 s in view. Done: the page and the star field ask one decision whether anyone can see them, with #740's cover, and the page starts a reading as soon as it is uncovered. Covered, the page now takes 0.1 s a minute. |
| #741 | Classic names draw through a second Direct3D device: about 53 driver threads and 74 MB of GPU memory for the life of the process. Done: each name is drawn through the map's own device, and drawn again on the device that replaces a lost one. After Settings draws its map preview, Classic lettering runs 126 threads against 127 for Standard, where it ran 179. |
| #738 | A scan leaves about 600 MB committed: one collection at idle takes it from 946 to 338 MB. The structural part waits on a decision. |
| #742 | The Launch timing pair misses the 380-823 ms before `OnLaunched`. Do it before measuring #541. Done: the pair starts at the entry point, before `App`'s statics read the stores, and its start carries the milliseconds since the process was created. Over four warm Release launches on an invented fixture, the runtime's start took 64-112 ms and the entry point to `OnLaunched` 324-398 ms, both outside the old span. |
| #743 | A composite ReadyToRun publish jits about 540 methods fewer, for 35 MB. Measure again after #541. Waits on a decision. |
| #703 | Each tile arrival rereads the pointer and rebuilds the outline: 746 ms of UI thread over three maximized zoom cycles. |
| #704 | `RowGhosts.Leave` lays out the whole tree for each leaving row: 65-260 ms per change. |
| #705 | `ExploreRow` builds every row's full path at build time: 161-171 ms per rescan of 22,431 rows. |
| #701 | The Hugging Face row's live-tree query omits `LockFilesListed`: 20,100 probes, 117-221 ms and 40 MB. |
| #710 | MFT attribute lists are read one cluster at a time. Merging touching clusters halves the reads. Done: lists in cluster order whose clusters touch or are no more than `MftReadGap` apart are read as one, up to the largest a list can be (`MftListReadPlanner`), and a read of several that fails is made again for each list alone. On one shared machine, warm, with 1 MiB reads and 8 list reads in flight, three runs each: the list reads of C: fell from 33,792 to 13,977 and their stage from a median of 1.57 s to 0.83 s, Explore from 6.26 s to 5.56 s; those of P: from 28,513 to 8,809, 3.58 s to 0.81 s, and Explore from 5.85 s to 2.97 s. |
| #698 | The Topaz row reads 383 uninstall keys before it probes its folder: 28-42 ms. |
| #700 | The Capture One row examines its sessions twice per pass (code-read estimate). |
| #693 | pnputil `/devices` costs about 1 s of kernel processor time per pass. Done: pnputil lists the packages without `/devices`, and which are in use comes from every device's `DEVPKEY_Device_DriverInfPath` and `DEVPKEY_Device_ExtendedConfigurationIds`, phantoms included, read in process (`DeviceDrivers`). Where a device does not read, or the store holds an extension package and no device reports its extensions, pnputil is asked again with `/devices`. On one machine, unelevated, the reading took 12-23 ms for 252 devices and named the same 31 packages in use as pnputil, 3 of them extensions, and the listing fell from 1,585 ms to 290 ms. |
| #699 | Windows.old is measured in full (96-172 s elevated) to ask whether it is empty. Waits on a decision. |
| #702 | The Classic names hold 79-83% of a core while a maximized map pans. Waits on a decision. |

### Waiting on a decision

| Issue | The decision |
| --- | --- |
| #693 | Keep pnputil's `/devices` answer, or read device properties in process and accept that extension INFs need their own source. `safety`. Decided: read in process, with `DEVPKEY_Device_ExtendedConfigurationIds` as the extension INFs' source, and pnputil's `/devices` answer wherever that reading cannot vouch for every package. |
| #696 | Keep transcript readings across passes, keyed by length and write time, against the recorded "read afresh" (#531). |
| #699 | Ask Disk Cleanup's handler for emptiness without its full measure, or take Windows.old's size from the scanner. `safety`. |
| #702 | The Classic names' look against their cost: two expressions and an image source per name. |
| #738 | Who owns an `ExploreTree`'s arrays, so that snapshots can share and hand back column buffers instead of allocating new ones. |
| #739 | Static bounds while the tracker rests: whether a pinch's first frames against the bounds from before it started are invisible. Decided: pinned wide enough for any scale it rests at, so those frames are free rather than held. An injected pinch at an edge showed no jump and the same frames as the build before it. |
| #743 | 35 MB of download and disk against about 65 ms of start-up. |

### Checked and not filed

| Finding | Why no issue |
| --- | --- |
| `BuildDirectoryProvider` recognises its candidates again for its declaration | 1-15 ms wall once per pass on a real source tree. |
| The Chromium AppData walk in a Clean preview | About 8,500 listings, 138-186 ms wall and 36 MB per pass, shared by both rows. The depth-6 search is the design (#43, #61, #647), and #661 asks whether to narrow it. |
| The first row to ask for declarations pays for every row's | The `RowDeclarations` design, paid once per pass and shared. |
| Claude Code sidecar dating, Cargo and node_modules measurement | 69-159 ms, bounded and parallel, or the walk primitive at about 14 µs per entry. |
| Crash dump signature reads, emulator obstacle probes | Microseconds, or 13 ms per pass. |
| Git LFS per-object measure | No LFS store on the machine, and the claim that it is serial was not confirmed. |
| Resolve walks each render folder twice | About 10 ms even for a 100 GB cache, and it needs the scanner seam #465 and #529 ask for. |
| Restore points' three readings in turn, `TempFolderListings` building paths eagerly | Under 60 ms, elevated only, or 9.4 ms and 5 MB once per pass. |
| Steam artwork measured as 1,965 requests | 130 ms against 115 ms for one walk of the parent. |
| VS Code discovery in three rows, pre-commit listing twice, packaged-app probes | Under 1-40 ms each, the third audit's "repeated listings" row. |
| The temporary files row's 2.4 s of processor time | One walk of a 497,000-entry `%TEMP%` at the same per-folder rate as Cargo, NuGet and Gradle. |
| 74 absent rows | 88 ms of processor time and 2.2 MB in total. |
| One conhost per tool start, Bun's two queries in series | 29 starts per pass at about 5 ms each, and avoiding them risks visible windows. Bun's pair is about 40 ms per pass. |
| Git's `cmd\git.exe` launcher, DISM started unelevated, Python tools starting two interpreters | 10-16 ms per start, one deliberate start that answers 740, or how those tools are installed. |
| Auto reads the file table alone for a volume root | Warm, a walk is faster on some volumes. Cold, the table wins on most, and the walk costs about eight times the processor time. |
| Cold file-table reads, folders of 95,000-149,000 entries, 260-character paths, OneDrive placeholders | 0.46-2.7 GB/s on a loaded disk, and no errors or outliers. |
| The duplicate search's storage cleans | 3.5 s warm, overlapped with finding. |
| Clean page row arrivals | 115 ms over a 65 s preview, p50 3.9 ms per arrival. The second audit's verdict stands, now measured. |
| The item list's search over 1,965 items | 140 ms for a search and its clear, in line with the third audit's figure. |
| The pointer light, GPU and UI-thread sides | GPU busy 3.7-8.5% of hover time, and under 2 ms of UI thread per move. |
| `ExploreLift`'s shadow and mask textures per hover | 15-30 small textures a second, inside the compositor's hover load. A lift sized to stable buckets would remove them. |
| `CushionRenderer` uploads, a canvas-sized surface per drawing | Uploads only when the quads change. The surfaces are WinUI's own. |
| ReadyToRun coverage, `ExtendsContentIntoTitleBar`, the scan-choice list in the Storage page's constructor | Every image that matters is ReadyToRun, 27 App and Core methods are jitted (about 5 ms), and the rest is native WinUI work or 15-30 ms. |
| Two NVIDIA driver threads waking about 79 times a second | The driver's behaviour, not Deguffer's. |
| #524 | Measured: a tool window left open is now built after the first frame, as the fix intended (162-234 ms). |
| A collapsed indeterminate progress bar on Explore, camera expressions reading the property set they write | Compositor wake-ups unchanged without either. #739 names the cause. |
| The names, `Follow`, overview, strokes, shadow and `BarMotion` expressions | Each follows #739's tick and still ticked with its own switch off. |
| Idle Duplicates, Startup, Installed apps and Storage before any map | 31-109 ms of processor time per 45 s. |
| The paused star field after leaving About, Storage at 16-24% after About | 0.06-0.11 s a minute, and the second does not reproduce in a fresh process: it was #739's tick. |
| The Memory page's own reading | About 1 s a minute of managed work. Most of its idle cost is #739's tick on its own map. |
| A UI Automation client holding the map's automation peers, x:Bind `StopTracking` alone, GC `ConserveMemory` or `RetainVM` | Disproved by heap dumps and probe builds for #737 and #738. |

Not covered: spinning, removable and network storage (none on the machine), ARM64, a true 3840x2160
window (the largest was 4758x1408), frame times during a scan snapshot (UI-thread stalls were
measured instead), Composer, Vagrant, Docker, LM Studio and Scoop (not installed), tool starts that
only a clean makes (no clean was run), approved source folders (none configured), Capture One and
Git LFS stores (absent: estimates only), a duplicate search of a 2 TB volume, the questions of #510
and #484 (no run varied the reader count or timed an index build inside a preview), a WPR stack of
the Settings page's heaviest idle bursts and a standby-cleared cold launch (both need a quiet
machine), and whether the first map of an `--explore` launch reaches the screen within 8 s (#523's
traces ended first).

## Fifth audit: every hot path again, and the rules that stop the classes recurring

A fifth audit read the whole product source again: the walk and its primitives, the
file-table route, Explore's model, layout and drawing, the WinUI shell, the providers in two halves,
planning and the clean with their safety checks, the Core subsystems outside Clean and Explore, and
a sweep of every file for recurring classes of cost. A second pass checked each finding against the
code, against every open and closed issue, and against the four tables above, and rejected or merged
what did not hold. The survivors were filed as #1014 to #1046, and eight findings that extend an open
issue were added to it as comments. Some figures were timed with throwaway programs outside
Deguffer, against copies of the code or the Release `Deguffer.Core.dll`, on a shared machine; each
issue says which. No change was measured inside Deguffer.

### What it found, in one paragraph

**The per-entry walk is lean; the costs left are questions asked again and safety checks run one at
a time.** Every planning pass waits for the registered-disk readers before any row starts, and the
Hyper-V reader starts PowerShell even for an account it will refuse (#1014, #1038). After a clean,
the §5.6 verification follows every surviving protected path to its mount point when nothing was
left standing (#1016), and the checks before and after a clean probe paths one at a time (#1017,
#1018). Most rows probe again for §5.6 the paths their own listing returned (#1039). A file-table
scan copies its whole table at every snapshot (#1015), every walk boxes an enumerator per directory
(#1021), and the map rebuilds per-drawing facts on the UI thread (#1026). These classes recur, so
`CLAUDE.md` G4 now states them, and the source sweep holds the mechanical ones.

### Order of work

#1049 tracks the largest gains across all five audits, in the order to work them.

| Issue | Note |
| --- | --- |
| #1016 | §5.6 verification follows every survivor to its mount point when nothing was left standing. `safety`. The largest gain for a clean: 1.4-2.0 s estimated for 10,918 paths, from measured per-path costs. |
| #1014, #1038 | Planning waits on every registered-disk reader in turn, and the Hyper-V reader starts PowerShell to learn the account will be refused (226-298 ms measured for the role check alone). |
| #1015 | File-table snapshots copy all nine columns: 26-41 ms and 100 MiB per 3 million slots against 4.7-5.4 ms and 14 MiB for flags and parents only, measured on stand-in arrays. |
| #1017, #1018, #1019 | §5.6 checks and the Clean press's narrowing probe paths one at a time, the last on the UI thread; registered storage follows every step live. `safety`. |
| #1039 | `ProtectAsync`'s default takes no listing, so most rows probe again what they listed. The seam fix for #755's leftovers. `safety`. |
| #1021, #1020, #1022, #1032 | Per-directory and per-file costs in the walk, the removal walk, Explore's occupancy probe and file targets. #1020 and #1032 are `safety`. |
| #1026, #1027, #1028 | Per-drawing facts rebuilt on the UI thread, layouts copying unsized lists, and the overview's second upload. |
| #1033, #1034, #1035, #1023 | The duplicate search: judging inside the shared lock, the checksum store's load, stream listings per copy, and linear place lists per folder. |
| #1029, #1030, #1031 | UI-thread churn: progress by count, Duplicates page state per group, a bound list cleared and refilled. |
| #1036 | Files other programs wrote, read in full with no bound. `safety`. |
| #1024, #1025, #1037, #1040, #1041, #1042, #1043, #1044, #1045, #1046 | Smaller allocation and repeated-question costs in the file-table index, providers and Explore's policy build. |

### Added to open issues

| Issue | What was added |
| --- | --- |
| #677 | The per-shape Oklch conversion and the lock every tile worker shares, which #677's fix leaves (8-25 ms per 96,000 shapes measured, plus contention). |
| #480 | Release the link queue once `PlaceLinks` has read it. |
| #684 | The checksum cache's bound applies only when it writes, so one search's peak is unbounded. |
| #787 | Three shifting inserts per arriving group. |
| #448 | A capturing lambda per tool lookup, for the single-flight fix to remove. |
| #447, #535 | Explore and the duplicate search build providers afresh, so every scan starts the tools' location queries again. |
| #444 | The tracked-file check builds a string for every output line and candidate pair. |

### Checked and not filed

| Finding | Why no issue |
| --- | --- |
| The placeholder walk's mode switch per listing call and basic-info read per placeholder | Deliberate: the switch is a per-thread flag, and the read is the eligibility check at release (#355). |
| `RootSetWalk`'s per-directory arrays | `everyRoot` carries each directory's folders to its children by design (#473); `opened` is about 48 bytes. |
| `MapTile.Outlines` scanning its tile's shapes per hover | #678 decided a tile is small enough. |
| `ExploreRow.KeyOf`'s name string per child per snapshot | #680 weighed a span lookup and chose one key per child. |
| The About page's star field built per visit | The third audit's verdict stands. |
| Preference writes on the UI thread | About 1 ms per click, the third audit's verdict, now measured again. |
| Driver store dates beside its measurement, `DeclaredLocations` probes, repeated listings in Cargo, Godot, LM Studio and installer payloads | 1-30 ms per pass, hidden behind pnputil or a handler survey, or presence-only savings under 10 ms. |
| The other-accounts row measuring live profiles | #359 requires every profile's size on the report. |
| Recorded refusals checked place by place | Zero to a few places, each already parallel. |
| Disk Cleanup handlers surveyed one after another in the setup rows | Whether the handlers tolerate concurrent calls is not known. |
| Steam rows ignoring the token in their loops | Bounded by the library count. |
| `DllImport` with runtime marshalling in the Memory, window and WMI calls | Per action or reading, not per entry; the source sweep's `library-import-not-dll-import` rule holds new code. |
| `SequentialScan` on the duplicate search's first-and-last-blocks read | Disproved: no read-ahead past the 64 KiB block either way. |
