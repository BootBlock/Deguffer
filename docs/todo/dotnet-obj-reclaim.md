# Reclaiming .NET `obj` directories

> **Status:** 🟢 ACTIVE — specified and ready to implement; no code written yet.
> **The specification lives in [issue #10](https://github.com/BootBlock/Deguffer/issues/10), not
> here.** This file is a pointer so the effort is discoverable from `docs/todo/`; copying the spec
> would give it two homes and one of them would go stale.

## Why the spec is on the issue

Every other plan in this directory predates the work it describes and was written to be argued with
before anyone started. This one is the same document, kept on the issue so that discussion,
revisions and the eventual implementation all attach to one thread. Treat the issue as the plan doc:
it carries the tier decision, the recognition rule, the discovery design, the type layout, the
settings question and the G8 verification list.

`docs/todo/_spec.md` still outranks it on anything to do with the safety model.

## The shape of it, in three lines

- **Tier 1**, pre-selected: nothing in a recognised `obj` is unique, and the next build regenerates
  it with no explicit user command.
- **Recognition is by triangulated identity, not by name.** 41% of directories named `obj` on the
  surveyed machine were not SDK intermediate output, and one held third-party 3D art assets.
- **Discovery is user-configured roots**, with the MFT index as an accelerator where elevation makes
  it available. A cheap index makes discovery free; it does not make consent implicit.

## When this lands

Flip the banner to ✅ COMPLETE and `git mv` this into `done/`, leaving the pointer intact — the
issue stays the record of what was decided and why.
