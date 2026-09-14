# A memory view beside Explore — plan

> **Status:** ✅ COMPLETE — phase 1, a read-only view, landed on 2026-09-13, and every step of it is
> closed under [#123](https://github.com/BootBlock/Deguffer/issues/123). Phase 2, the close action, is
> specified in [_spec.md](../_spec.md) §7.2.1, which landed on 2026-09-13 before any of its code;
> its first three steps landed on 2026-09-13 and the Memory page itself on 2026-09-14, under
> [#129](https://github.com/BootBlock/Deguffer/issues/129). Both phases are built, so the plan is
> finished. What §7.2 and §7.2.1 of the specification require of the view is the live record, and
> "Limits that stay open" below states what was never measured.

On 2026-09-12 the maintainer decided to widen Deguffer's scope to show where memory goes, on the
strength of [memory-view.md](../memory-view.md). That document is the record of the investigation and
stays as it was written. [_spec.md](../_spec.md) §1, §2, §7.2 and §7.2.1 state what the view is and what
it will never do. This one states the order it is built in, and what each step has to prove before it
lands.

## Phase 1: a read-only view

Each step lands on its own, in this order, because each depends on the one before it.

| Step | Issue | What lands |
| --- | --- | --- |
| 1 | [#124](https://github.com/BootBlock/Deguffer/issues/124) | §1, §2 and §7.2 of the specification, and this plan. No code. |
| 2 | [#125](https://github.com/BootBlock/Deguffer/issues/125) | A snapshot source in `Deguffer.Core.Memory`, behind a seam with a fake: the process table, the system figures, the memory lists and the service hosts. |
| 3 | [#126](https://github.com/BootBlock/Deguffer/issues/126) | The memory tree built from one snapshot: Applications, Services and Windows. |
| 4 | [#127](https://github.com/BootBlock/Deguffer/issues/127) | A seam over what the layouts and surfaces read, so the memory tree is drawn by the code that draws Explore. |
| 5 | [#128](https://github.com/BootBlock/Deguffer/issues/128) | The Memory page, with no action. |

### What each step has to prove

**The snapshot source.** The process table is read in one call, into a buffer that is reused, and
parsed with every offset and every string checked against the length Windows returned. The parser
is proven on synthetic buffers that end early, point past their end, or name a string that runs
past it. The two undocumented figures, the private working set and the creation time, are proven to
be checked against Deguffer's own process through the documented counter where Windows has it and
through the documented fallback where it does not, and to turn off together, rather than to read as
zero, when either check fails. The memory lists are used only where they agree with
`GetPerformanceInfo`. The snapshot records that the service list may omit services this account may
not query. No fixture carries a process name or a figure measured on a real machine.

**The memory tree.** A parent link that the creation times do not allow is proven to be refused,
including an identifier reused by a later process. An orphan is proven to sit at the top of its
part, and a process with children to draw its own share as a child. Every service host is proven to
sit in Services, whatever its parent is, and what a host started to sit in Applications instead. The
remainder is proven never to be negative: where the
figures add up to more than physical memory, the tree is proven to state by how much, and to draw a
remainder of zero rather than one below it. A node is
proven to keep its identity across two snapshots by process identifier and creation time, and to
lose it when the identifier is reused.

**The layout seam.** Every existing Explore test passes unchanged, and the memory tree is proven to
lay out through the same treemap, icicle and sunburst.

**The page.** It is driven, not only built: the headline, the three parts, the remainder, each note,
a refresh that keeps the user where they were, and the page with the backdrop switched off (§6.5).
It offers no action of any kind.

## Phase 2: the close action

[#129](https://github.com/BootBlock/Deguffer/issues/129) tracks it. [_spec.md](../_spec.md) §7.2.1
specifies it, and that specification landed before any of the code, as the issue required: the
program's own close and nothing stronger, posted only to the visible, unowned, uncloaked top-level
windows of a program in the user's own session that owns no console window, to a process held open by
a handle and checked again immediately before each message, with every refusal stated with its
reason, commit charge reported before and after, and §5.6 met by separating what can be asserted
exactly from what can only be listed, rather than by a claim that nothing else stopped. Memory never
controls a service (§2),
whatever the open question about service control in
[unreached-locations.md](../unreached-locations.md) comes to decide for disk.

Each step lands on its own, in this order, because each depends on the one before it.

| Step | Issue | What lands |
| --- | --- | --- |
| 1 | [#129](https://github.com/BootBlock/Deguffer/issues/129) | §7.2.1 of the specification, and this section. No code. |
| 2 | [#131](https://github.com/BootBlock/Deguffer/issues/131) | A seam over what Windows says about **one** process beyond its memory figures: its session, its account, its integrity level, its criticality, its package state, and its top-level windows with their classes. |
| 3 | [#132](https://github.com/BootBlock/Deguffer/issues/132) | `MemoryActionPolicy`, `ProcessCloser`, the confirmation's words and the close's report, with §7.2.1's §5.6 evidence. |
| 4 | [#133](https://github.com/BootBlock/Deguffer/issues/133) | The Memory page acts: a selection, a refusal stated on it, a confirmation, and a report the next reading does not wipe. |

### What each step has to prove

**The process facts.** Every answer is proven to have three values rather than two — yes, no, and
could not be read — and the third is proven to reach the policy as a value of its own rather than as
a false, because §7.2.1 refuses on it exactly as it refuses on the first. The seam is proven to open
one process for one row, so a picture of five hundred opens none. The window survey is proven to keep
only top-level, unowned, visible, uncloaked windows, to refuse the whole process on a console class
rather than dropping the one window, and to answer with an empty set rather than a null. Which
suspension route an unelevated Deguffer may ask is measured before either is relied on. No fixture
carries a real process name, account or machine name.

**The policy and the closer.** Every row of §7.2.1's refusal table is proven refused, with the reason
that row states, and in the table's order where more than one applies. The unrecognised case is
proven: a process with no window that qualifies is refused rather than attempted. The second decision
is proven to bite, by a creation time that differs between selection and action and by a window whose
owning process changed between the survey and the post. `WM_CLOSE` is proven to be the only message
that can leave the type. The §5.6 evidence is proven on all four counts: the record of what was
posted and where; the shell's owner or this session's compositor missing afterwards failing the run
and naming itself; descendants named as expected; and every other exit — a service host, an
`explorer.exe` that is not the shell, and a process Deguffer started included — reported as one
Deguffer sent nothing to rather than failing the run.

**The page.** It is driven, not only built: a refusal sentence for each of several refused kinds, a
confirmation naming the program and how many windows will be asked, a cancel that leaves the process
untouched, a report still on screen after several readings, no second close offered while one is
watched, and the page with the backdrop switched off (§6.5).

## Limits that stay open

- **Nothing was measured elevated.** The investigation ran unelevated, and so does phase 1.
- **The exact breakdown of physical pages probably needs elevation.** That was inferred from
  published evidence and not tested, so phase 1 puts what it cannot separate in the remainder and
  does not offer an elevated view of it.
- **`SystemBasicProcessInformation` is not used.** Its documentation contradicts itself about its
  structure, and it is newer than Deguffer's minimum Windows version. Phase 1 identifies a process by
  identifier and creation time.
- **A snapshot is stale as soon as it is read.** Phase 1 acts on nothing, so staleness costs only an
  out-of-date picture, which the page refreshes. Phase 2 holds the target open by a handle and checks
  again before it acts.
- **A console window is recognised by a class name Microsoft does not document.** Both names come
  from the terminal's own source. §7.2.1 states the residual risk and why the alternative is worse.
- **The console refusal rests on one observation outside the investigation.** On 2026-09-13, an
  unelevated enumeration of top-level windows on one workstation found an invisible
  `ConsoleWindowClass` window reported against a program that is not a console program. That is the
  whole of the measurement: one machine, one window, nothing closed.
- **The suspension route was measured, and the per-process one was taken.** §7.2.1 required this
  before either route was relied on. Unelevated on one workstation on 2026-09-13, a process snapshot
  that captures nothing (`PssCaptureSnapshot` with `PSS_CAPTURE_NONE`, then
  `PSS_PROCESS_FLAGS_FROZEN`) answered for all 285 processes a
  `PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE` handle could be had for, 30 of them packaged.
  `IPackageDebugSettings::GetPackageExecutionState` also answered unelevated for all 30, but it
  describes a *package* rather than a process: it reported `PES_UNKNOWN` for 14 of the 30, one of
  which the snapshot reported frozen, and it reported one package suspended while one of that
  package's two processes was not frozen. It also needs an interface whose other methods suspend and
  terminate applications, and §7.2.1 has one verb. So the per-process route is what a packaged
  process's state is read with, and the per-package one is not declared at all. The documentation
  states no access right for capturing nothing, so what an unelevated Deguffer may ask rests on that
  measurement rather than on a documented contract.
- **Whether a message posted to an elevated program is dropped was not measured.** Microsoft
  documents that the filter blocks it and that the call may report success anyway, so §7.2.1 refuses
  by the integrity level rather than by what the post reports.
- **The service refusal is not a complete rule, and §7.2.1 says so.** Windows leaves services this
  account may not query out of the list without an error, so a host it did not name is drawn as an
  ordinary process and the service row does not reach it. What refuses such a host is the rest of the
  table rather than that row.

## When it is finished

When phase 1 lands, the README's description of Deguffer and its roadmap say that it shows where
memory goes. This plan moves to `done/` when phase 2 has landed, or when a decision not to build it
is recorded here.
