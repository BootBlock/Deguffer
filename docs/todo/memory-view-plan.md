# A memory view beside Explore — plan

> **Status:** 🟢 ACTIVE — phase 1, a read-only view, is being built, and is tracked in
> [#123](https://github.com/BootBlock/Deguffer/issues/123). Phase 2, the close action, is not started,
> and is specified in [_spec.md](_spec.md) §7.2 before any of it is built.

On 2026-09-12 the maintainer decided to widen Deguffer's scope to show where memory goes, on the
strength of [memory-view.md](memory-view.md). That document is the record of the investigation and
stays as it was written. [_spec.md](_spec.md) §1, §2 and §7.2 state what the view is and what it
will never do. This one states the order it is built in, and what each step has to prove before it
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

## Phase 2: acting (not started)

[#129](https://github.com/BootBlock/Deguffer/issues/129). Nothing of it is built until §7.2 specifies
it. Sections 7 and 10 of [memory-view.md](memory-view.md) state what that specification has to
settle, and item 3 of section 10 lists all of it together: the
program's own close and nothing stronger, only to the visible top-level windows of a windowed
application in the user's session that belong to no console host, the target identified by
identifier and creation time and checked again immediately before anything is sent, every refusal
in that section's table stated with its reason, commit charge reported before and after, and how
§5.6 can be honest when processes exit on their own. The memory view never controls a service
(§2), whatever the open question about service control in
[unreached-locations.md](unreached-locations.md) comes to decide for disk.

## Limits that stay open

- **Nothing was measured elevated.** The investigation ran unelevated, and so does phase 1.
- **The exact breakdown of physical pages probably needs elevation.** That was inferred from
  published evidence and not tested, so phase 1 puts what it cannot separate in the remainder and
  does not offer an elevated view of it.
- **`SystemBasicProcessInformation` is not used.** Its documentation contradicts itself about its
  structure, and it is newer than Deguffer's minimum Windows version. Phase 1 identifies a process by
  identifier and creation time.
- **A snapshot is stale as soon as it is read.** Phase 1 acts on nothing, so staleness costs only an
  out-of-date picture, which the page refreshes. Phase 2 must check again before it acts.

## When it is finished

When phase 1 lands, the README's description of Deguffer and its roadmap say that it shows where
memory goes. This plan moves to `done/` when phase 2 has landed, or when a decision not to build it
is recorded here.
