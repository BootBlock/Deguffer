# A progress display for the preview scan — investigation

> **Status:** 📘 REFERENCE — the investigation is finished. It answers "can the Clean preview show
> how far through it is?" with *partly, and not by the rule the clean uses*. The measurements, the
> outside evidence and the reasoning are here so the candidate routes can be argued rather than
> re-derived. Re-measure the timings before trusting them against a very different machine.

Deguffer shows an indeterminate ring while a Clean preview runs, and a determinate bar while a clean
executes. This asks whether the preview can have a determinate bar too, what a truthful denominator
would be, and what one would cost.

**The short answer: a preview has countable structure, but not one number.** The pass has an exact
provider count that nothing uses; one of its two measurement routes states its own record count and
that count is thrown away; the other route has no denominator at any layer. The rule the *clean*
uses to build its bar — weight each part by the bytes it expects to move — was measured here against
a preview and is actively wrong for one.

## 1. What the app does today

| Surface | While scanning | While cleaning |
| --- | --- | --- |
| Clean page | `ProgressRing`, indeterminate, bound to `IsBusy` | determinate `ProgressBar` and a written percentage |
| Explore page | determinate on the file-table route, indeterminate on the walk | — |

Explore already answers this question for a whole-volume scan, and answers it both ways in one
control. `ExploreScanner` reads `IMftSource.RecordCount` before its first record and reports a real
fraction against it; the walk reports a rising item count with `Total: null`, and the same bar goes
indeterminate. The rule is written down in five places in the tree — `ExploreScan.cs`,
`ExploreViewModel.cs`, `ExplorePage.xaml`, `CleanViewModel.cs` and `CleanPage.xaml` — all saying the
same thing:

> a walk cannot know how many directories it has yet to open, so that one is honest about being
> indeterminate rather than inventing a denominator.

Nothing in [_spec.md](_spec.md) requires a bar anywhere. §5.5's only clause about a running scan is
"**Stream partial results to the UI — never block on a complete scan**", and §6.5 is why every
percentage the app draws is also written out in text.

## 2. What comparable tools do

Every disk tool surveyed lands in the same place, and two of them encode the rule Deguffer arrived
at independently.

**WinDirStat refuses a percentage when its total is untrustworthy, and does so in code.** Its
denominator is *bytes in use on the volume*, from `GetDiskFreeSpaceEx`, which is known before the
walk starts; `GetProgressRange()` returns `0` for a folder or a file. When junctions or mount points
are not being excluded, [`MainFrame.cpp`](https://raw.githubusercontent.com/windirstat/windirstat/master/windirstat/MainFrame.cpp)
sets the range to zero deliberately, with the comment *"Directory structure may contain other volume
or internal loops so set range to indicate there is no range so display pacman"*, and shows the
Pacman animation instead of a bar. The numerator is clamped, too, because hard-linked files count
twice. Even so, its [changelog](https://github.com/windirstat/windirstat/blob/master/CHANGELOG.md)
records fixing a bar that dropped *"from 100% back to 99% at scan completion"*.

**SpaceSniffer states the same conditional in prose.** From its manual: *"Since the total size to be
scanned is known only if you select a drive, the progress bar is shown only if you select a drive
path. In all other cases, a simple message will be displayed."*

**Everything's author says the problem in as many words.** On the voidtools forum
([t=11623](https://www.voidtools.com/forum/viewtopic.php?t=11623)): *"Displaying a progress bar is
difficult because Everything doesn't know how many files/folders there are until the scan
completes."* Its indexing bar therefore counts **steps, not files** — *"x of y volumes indexed"* —
and the author adds that it *"is not really accurate"*. A user
([t=12793](https://www.voidtools.com/forum/viewtopic.php?t=12793)) reported it stuck at 33% and then
75% for days; the shipped fix was to read folders alphabetically so that the *displayed path* became
the usable progress signal. That is a fix to the readout rather than to the percentage, and it is
the failure mode a made-up denominator produces.

**The tools with no denominator show a count or an elapsed time instead.** Filelight shows
`"N Files, <size>"` beside an indeterminate placeholder. QDirStat shows `"Reading… <elapsed time>"`,
refreshed every 200 ms. ncdu shows `"Total items: N"`, a size, and the current path. gdu shows
items, size and elapsed time. BleachBit's fraction is over **cleaner operations**, and its
maintainer deliberately put the running byte total in the status bar rather than the progress bar
([Launchpad #392302](https://bugs.launchpad.net/bleachbit/+bug/392302)).

**git implements exactly the count-versus-fraction switch this question is about.** In
[`progress.c`](https://raw.githubusercontent.com/git/git/master/progress.c), a known total formats as
`"%3u%% (%"PRIuMAX"/%"PRIuMAX")"` and an unknown one formats as a bare counter — which is why you
see `Enumerating objects: 2369, done.` and then `Counting objects: 100% (2369/2369)`.

**DaisyDisk is the counterpoint on the other technique.** It abandoned a live sunburst drawn during
the scan: *"All we got was just a convulsing set of rings… Fail."* Deguffer's Explore walk draws
partial trees anyway, and answers that by ordering a snapshot by name rather than by size — which is
recorded in `ExploreScanner`.

WizTree, TreeSize, CCleaner, `cleanmgr` and Storage Sense could not be pinned down: no developer
statement or documentation was found for any of their denominators. TreeSize is the one disk tool
found doing previous-run estimation, and only for file *search*, where its changelog says the bar is
estimated from statistics of past searches.

## 3. What the guidance and the research say

Three things in this material bear directly on the decision, and one of them cuts against the
position the codebase currently states.

**The strongest guidance is that inaccuracy alone is not a reason to go indeterminate.** Microsoft's
[Win32 UX guide](https://learn.microsoft.com/en-us/windows/win32/uxguide/progress-bars) says: *"Use
determinate progress bars for operations that require a bounded amount of time, even if that amount
of time cannot be accurately predicted… Don't choose an indeterminate progress bar based only on the
possible lack of accuracy alone."* Indeterminate is for an operation that *"access[es] an unknown
number of objects"* — which the walk genuinely does, and which the provider pass genuinely does not.
The same page endorses the hybrid: *"you can use an indeterminate progress bar while the objects are
counted, and then convert to a determinate progress bar."* This is worth stating plainly, because it
is the strongest argument *against* the ring, and the tree does not currently record it.

Its other rules are the ones already followed here: *"Always increase progress monotonically"*,
*"Don't let a progress bar go to 100 percent unless the operation has completed"*, and *"the
progress bar should be set initially to at most 33 percent"* where a long stall would otherwise
follow. Note that these rules exist only on the unmaintained Windows 7 page; the current
[WinUI progress-controls page](https://learn.microsoft.com/en-us/windows/apps/design/controls/progress-controls)
states none of them, and reframes the choice around whether the operation blocks the user.

**A misleading bar measurably costs more than no bar.** Conrad, Couper, Tourangeau and Peytchev,
*The impact of progress indicators on task completion* (*Interacting with Computers* 22(5), 2010,
[open access](https://pmc.ncbi.nlm.nih.gov/articles/PMC2910434/)), measured abandonment against
progress feedback: a bar that started slow and sped up produced **21.8%** breakoff, one that started
fast and slowed produced **11.3%**, and **no feedback at all produced 12.7%**. The bar whose early
behaviour understated progress was roughly twice as bad as no bar. That is the measured version of
the instinct already in the tree, and it is the reason a wrong denominator is not a small cosmetic
error.

**The perceptual work is narrower than its reputation.** Harrison et al., *Rethinking the Progress
Bar* (UIST 2007, [PDF](https://chrisharrison.net/projects/progressbars/ProgBarHarrison.pdf)), found
that pauses make a bar feel slower, that the effect is exaggerated near the end, and that an early
pause is *"essentially equivalently preferred to the linear function"* — an early stall is close to
free. Every bar in that study lasted 5.5 seconds, no backwards-moving function was tested, and the
authors' own discussion says *"processes with known static completion conditions and stable progress
are not good candidates"* for their technique. The follow-up, *Faster Progress Bars* (CHI 2010), is
about the fill's texture and not about progress behaviour at all. Later work disagrees about which
velocity profile feels fastest (Wang, Kang and Rau 2022, [arXiv:2211.13909](https://arxiv.org/abs/2211.13909),
find constant as good as accelerating). **The finding that survives every study is the narrow one: a
bar that appears to stall, especially late, is the reliably bad case.**

Two figures that circulate widely — "MIT Media Lab: 28% longer perceived duration" and "CMU: 37%
higher error rates" — have no traceable source and should not be repeated.

**A count with no denominator is a recognised answer, not a consolation prize.** Jakob Nielsen, on
[progress indicators](https://www.uxtigers.com/post/progress-indicators), uses a file-scanning
example: *"when the total is unknowable, show the running count anyway: 'Scanned 3,142 files so far'
is a numerator without a denominator, yet it still beats a naked spinner."* GNOME's HIG asks for a
label describing how much is done, with examples in exactly that form (*"13 of 19 images rotated"*).
No guidance treats a bare count as a substitute for *something moving*, though: all of them pair the
text with an indicator.

**Accessibility does not require a fraction.** WCAG SC 4.1.3 is satisfied by an announced busy
state — its own example is a screen reader announcing *"application busy"* — and technique
[ARIA25](https://www.w3.org/WAI/WCAG22/Techniques/aria/ARIA25) says outright that *"the use of an
ARIA progress bar is not actually important for this technique — what matters is the progress being
conveyed with a status message."* ARIA and UIA both define indeterminate by *omitting* the value
rather than by supplying a fake one. SC 2.2.2 excepts an animation that is not presented in parallel
with other content, which covers a preload spinner but is arguable for a marquee running beside
live UI.

One thing could not be established and needs observing rather than researching: **what Narrator
actually announces for a WinUI indeterminate `ProgressBar`.** Microsoft documents none of it, and
[microsoft-ui-xaml#1746](https://github.com/microsoft/microsoft-ui-xaml/issues/1746), reporting that
it announces *"0% progress bar"*, was closed as not planned. Given §6.5, that is worth driving
before any decision here is called finished.

## 4. Measured: where a preview's time actually goes

Twenty-four providers, run unelevated through `CleanupPlanner.CreateDefault()`, three consecutive
passes in one process on the developer's machine. Elapsed milliseconds between consecutive findings,
and the bytes each provider reported.

| Provider | p1 | p2 | p3 | Estimated bytes |
| --- | ---: | ---: | ---: | ---: |
| .NET intermediate build output | 1548 | 1945 | 1680 | 603,853,165 |
| Conda package cache | 1005 | 934 | 933 | 0 |
| PlatformIO download cache | 950 | 860 | 897 | 948,340 |
| npm package cache | 450 | 646 | 385 | 435,815,709 |
| pip package cache | 437 | 468 | 432 | 2,700,924 |
| NuGet package cache | 255 | 257 | 247 | 6,302,084,127 |
| GPU shader caches | 123 | 1 | 2 | 58,888,256 |
| uv package cache | 75 | 78 | 73 | 576,929,135 |
| Chromium application caches | 34 | 24 | 20 | 25,658,562 |
| Node.js project dependencies | 29 | 19 | 19 | 148,155,087 |
| Recycle Bin | 15 | 6 | 5 | 33,233,811 |
| vcpkg build caches | 11 | 8 | 9 | 0 |
| Crash dumps and error reports | 11 | 3 | 2 | 104,695,188 |
| Go build and module caches | 9 | 8 | 8 | 0 |
| pnpm store | 7 | 9 | 8 | 0 |
| Playwright browsers | 7 | 2 | 3 | 1,088,200,448 |
| Unity project library | 4 | 0 | 0 | 0 |
| Windows servicing logs | 4 | 3 | 2 | 31,575,761 |
| Gradle build cache | 2 | 0 | 0 | 0 |
| VS Code C/C++ IntelliSense cache | 2 | 0 | 0 | 0 |
| Cargo crate cache | 2 | 0 | 0 | 0 |
| Maven local repository | 1 | 0 | 0 | 0 |
| Rust build output | 0 | 0 | 0 | 0 |
| Python virtual environments | 0 | 0 | 0 | 0 |
| **Whole pass** | **4982** | **5274** | **4725** | |

These are the *warm, unelevated* shape, and a later pass in a live process is not a first pass in a
fresh one. [after-the-scanner.md](after-the-scanner.md) item 7 has the cold figures from a watched
run: an unelevated preview at 15.5 seconds, an elevated one at 28.8, of which building the volume
indexes is 9.9 seconds across seven volumes. What matters below is the proportions, not the absolute
times.

Four things follow, and between them they decide the question.

**The distribution is extremely skewed.** Three providers are 70% of the pass and five are 88%.
Fourteen of the twenty-four finish in under 20 ms. A flat `k / 24` bar is therefore not a
description of the run: after the first provider it would read 4% while 31% of the time had gone,
and its worst error across the pass is **27 percentage points**. That is the shape the Everything
author's users reported — a bar that reaches a number and sits there.

**Elapsed time does not track bytes.** Across the thirteen providers that found anything, the
Spearman rank correlation between elapsed milliseconds and estimated bytes is **-0.005**: no
relationship at all. Playwright measured 1.09 GB in 7 ms; NuGet measured 6.30 GB in 255 ms; conda
measured nothing whatever in 950 ms. A bar weighted by `ProgressWeights.For(...)`, the rule the
clean uses, would stand at **75% after six of twenty-four providers**, with 37% of the time gone.
Its worst error is 38 percentage points, and its error is in the direction Conrad et al. measured as
the expensive one.

**The reason is that the dominant costs are fixed rather than proportional.** The four slowest cache
providers spend their time launching another program and waiting for it: conda runs `info --json`
and then `clean --dry-run --json`, PlatformIO runs `system info --json-output`, and pip and npm each
ask their own tool where its cache lives. A Python CLI's start-up is most of a second whether the
cache behind it holds a gigabyte or nothing. §5.1 is why those calls exist at all, so this is a
property of the design rather than an inefficiency to remove.

**Per-provider cost is stable between runs.** Every expensive provider repeats to within a few per
cent across the three passes. That is the only measured evidence in favour of a remembered-duration
weighting, and it is a real one.

One further attribution matters. `SourceDirectoryDiscovery` is constructed once and shared by the
five source-tree providers, and it memoises its walk, so **the first of the five pays for all of
them**. That is visible in the table: `.NET intermediate build output` costs 1.5 to 1.9 seconds and
the other four cost nothing measurable. Any weighting that treats those five alike is wrong by
orders of magnitude in both directions.

## 5. What is countable today, and what is not

| Where | Quantity | Known when | State |
| --- | --- | --- | --- |
| `CleanupPlanner.PlanAllAsync` | 24 providers | before the pass | reported, but only as one sentence per provider |
| `CleanupProviderBase.MeasureAllAsync` | how many paths | before its loop | present, unreported |
| `MftVolumeIndexBuilder.TryBuild` | `source.RecordCount` | at volume open | known; **no callback exists** |
| `MftRecordStream.TryReadAll` | records read so far | per record | the handler already receives it; the builder discards it |
| `BoundedFileWalk.Visit` | this level's directory count | at each level | present, unreported |
| `ParallelEnumerationScanner` | bytes so far | per level | reported; **there is no entry count** |
| `ScanEstimateCache` | last measured size per path | at start-up | the only cross-run signal that exists |
| anywhere | elapsed time per path or per provider | — | **not recorded** |

Two gaps in that table are the substance of the answer.

**The §5.5 streaming path is wired and dead on the Clean side.** `IDirectoryScanner.MeasureAsync`
takes an `IProgress<ScanSize>`, `ParallelEnumerationScanner` reports one subtotal per breadth-first
level, and `DirectoryScanner` reports the remembered figure for first paint. Every production caller
passes `progress: null`. `CleanupProviderBase.MeasureAllAsync` is the only provider-level call site,
and it passes null, so the preview cannot currently show even "bytes seen so far" — which is the one
thing §5.5 asks for by name, and the readout Filelight, ncdu and gdu all settle on.

**The walk has no denominator at any layer, and the obvious heuristic is not monotone.**
`BoundedFileWalk.Visit` drains a queue level by level, so at the start of each level it knows that
level's width exactly and knows nothing at all about the next one. The fraction
`visited / (visited + pending)` falls whenever a level fans out more than the last did, and a bar
that goes backwards is the one thing every source here agrees a bar must never do. `ScanSize`
carries two byte counts and no entry count, so even an honest rising *count* on the Clean side needs
plumbing that Explore's walk reader has and this one does not.

## 6. The four candidate routes

### A. Say which provider, and how many are left

`PlanAllAsync` already reports `Checking {provider.Name}…` and already knows `_providers.Count`.
Turning that into `Checking npm package cache — 8 of 24` is exact, costs nothing, and claims nothing
about time. It is the only option here with no denominator problem, because the denominator is not a
claim about duration. It is the form GNOME's HIG asks for, the form git prints, and the form
Everything's author fell back to.

It does **not** justify a bar. Ordinal position is a truthful count and a false fraction, for
exactly the reason §4 measures.

### B. Wire the progress sink that already exists

Pass a real `IProgress<ScanSize>` down from the planner through `CleanupProviderBase`, so the
preview shows bytes accumulating while a large location is measured. This restores the behaviour
§5.5 asks for, uses machinery that is already built and already tested, and needs no denominator. It
turns a provider row from "blank until it finishes" into "filling in", which is what the ring is
standing in for today, and it is Nielsen's "numerator without a denominator" verbatim.

The MFT route reports once with its final figure rather than progressively, so this shows on the
walk and not on an elevated run. That asymmetry is a fact about the two routes rather than a defect.

### C. A real bar for the volume-index build

This is the one place a Clean preview has an exact denominator. `IMftSource.RecordCount` is known
the moment the volume opens, `MftRecordStream` already hands a record number to its handler, and
`MftExploreReader` already reports against exactly that every 65,536 records.
`MftVolumeIndexBuilder` discards the number it is given; adding a callback is one delegate
parameter, mirroring code that already exists a directory away.

It is worth more than it looks. On an elevated run the index build is the single largest lump — 9.9
seconds of a 28.8-second preview across seven volumes — and it currently happens silently inside
whichever provider first touches each volume. `RecycleBinProvider` names a bin on every fixed
volume, so on a multi-drive machine every drive gets indexed.

Two things qualify it. The denominator is *allocated* records, not records in use: NTFS never
shrinks the table, and one published forensic image showed 45% of its records free. That is the
right denominator for "records read", since the reader reads them all and a free record is cheap to
skip, but it is not a file count and must not be labelled as one. And it covers one phase of an
elevated run and nothing at all on an unelevated one, which §6.3 makes the ordinary case. A bar that
appears, completes, and is then replaced by a ring may be worse than a ring throughout — although
the Win32 guide endorses precisely the reverse transition, indeterminate first and determinate once
the total is known.

### D. Weight the pass by what it cost last time

The only route to a genuine end-to-end fraction. Record each provider's elapsed time, keep it beside
the sizes in `ScanEstimateCache`, and weight the next pass by it. §4 says the input would be stable
enough to be worth something, and there is shipped precedent: Jenkins estimates a build from the
last three successful ones and returns `-1` when it has no history, so the bar goes indeterminate
rather than lying; TeamCity requires five matching builds and disqualifies a history whose durations
are inconsistent.

It is also the largest change, and the one with the most ways to be wrong:

- **The first run has no history**, and neither does a run after a toolchain is installed or
  removed. Something has to be honest about that rather than guess, which by the rule already in
  `ProgressWeights` means falling back all-or-nothing rather than inventing a figure for the
  unmeasured part. Jenkins's `-1` is the same decision.
- **`ScanEstimateCache`'s file format changes.** `Load` treats a `JsonException` as a cache miss, so
  an incompatible file degrades to a cold start silently. Its `unchanged` short-circuit compares
  only the three size fields before skipping the save, so a duration that changed while the size did
  not would never be written.
- **`MeasureFromDiskAsync` is a different population.** It runs after a deletion, against a
  near-empty tree, and feeding its timings into a scan-cost model would poison it.
- **A duration is not a size**, so it sits inside rather than against the cache's stated rule that
  its values are for first paint only and are never the figure Deguffer acts on. Whoever implements
  it should confirm that reading rather than inherit it from here.
- **The denominator can move under the bar.** restic ships a scan-derived total that overshoots
  (`421780 / 421778 items` at 99.96%); Ninja's maintainer notes its total edge count is *"most
  commonly overestimated and then reduced"*. A remembered total is a stale total by construction.
- **Elapsed time is available with no new plumbing below the cache. An entry count is not** — no
  scanner counts entries, and the MFT route produces no count either.

## 7. What is rejected, and why

- **Weighting a scan bar by estimated bytes.** Measured at -0.005 rank correlation and a 38-point
  worst error, erring in the direction Conrad et al. measured as costing completions.
  `ProgressWeights` is right for the clean, where the bytes are known before the run starts and are
  what the run moves. In a preview the bytes are the thing being computed, so they are not available
  at the top of the pass in any case.
- **A flat `k / N` bar over providers.** A 27-point worst error, reading 4% at the moment 31% of the
  work is done.
- **A denominator derived from the walk's level queue.** Not monotone, so the bar goes backwards.
- **An elapsed-time or "time remaining" estimate.** Nothing records elapsed time today, and a scan
  whose cost is dominated by other programs' start-up is a poor thing to extrapolate from. The
  Win32 guide's own rule is to withhold an estimate rather than show an inaccurate one.
- **Drawing a partial picture as the progress indicator**, on the Clean page. DaisyDisk tried and
  abandoned it; Explore's answer to the same problem is a snapshot ordered by name, and the Clean
  page's rows already fill in as findings arrive.

## 8. Where this leaves the question

A determinate bar for the whole preview is possible only through route D, and only from the second
run onwards. Routes A, B and C are each smaller than D, each truthful on their own terms, and none
of them needs D to be worth doing. A and B together would replace "the app is busy" with "it is on
the eighth of twenty-four locations, and that location is up to 2.1 GB so far" — most of what a bar
would have communicated, and none of what it would have got wrong.

Two things should be settled before any of it is built. The first is what Narrator announces for the
ring today, which is a §6.5 question and needs driving rather than reading. The second is whether
the Win32 guide's *"don't choose an indeterminate progress bar based only on the possible lack of
accuracy alone"* changes the position stated in five places in the tree. On the evidence here it
does not — a preview pass genuinely accesses an unknown number of objects, which is that page's own
condition for indeterminate — but the tree currently argues from the absence of a denominator alone,
and that argument is weaker than the measurements are.
