# Deguffer — working agreement

`docs/todo/_spec.md` is the founding specification and the source of truth for the safety model,
the audit evidence behind it, and the decided toolchain. Read it before changing behaviour.
This file governs *how* the code gets written; the spec governs *what* it does.

## Engineering gates (mandatory)

These are gates, not preferences. A change that breaks one is not ready, regardless of whether it
compiles and passes tests.

### G1 — No monolithic files or objects

One reason to exist per type, one responsibility per file. If a type needs "and" to describe it,
it is two types. Apply SOLID where it earns its keep — particularly:

- **Single responsibility**: `DirectoryRemover` deletes; it does not decide *what* to delete.
- **Open/closed**: adding a cache source is a new `ICleanupProvider`, never an edit to a switch.
- **Dependency inversion**: Core depends on `IUserEnvironment` / `IProcessRunner` /
  `IProcessInspector`, never on `Environment.GetFolderPath` or `Process.Start` directly. This is
  what makes the safety rules testable without a package manager installed.

Soft ceiling of ~250 lines per file. Crossing it is a prompt to look for the seam, not a failure
in itself — but a 500-line file needs a stated reason.

### G2 — No god objects

No type that "manages", "handles", or "processes" the application. No service that both decides
policy and performs I/O. `CleanupPlanner` orchestrates providers and holds no cleanup knowledge of
its own; each provider holds its own rules and no orchestration. Keep that split.

### G3 — No AI-trope or junior-engineer code

Specifically banned:

- Comments that restate the code (`// increment the counter`). Comments explain *why*, or the
  non-obvious constraint — usually a spec section reference.
- Ceremonial abstraction: interfaces with one implementation and no test seam, factories that
  only call `new`, wrapper types that forward every member unchanged.
- `catch (Exception)` that swallows or rethrows unchanged. Catch the specific exceptions you
  expect, and say in a comment why they are expected.
- Defensive null checks on values that cannot be null, and re-validating an argument already
  validated one frame up.
- Speculative generality: no configuration knob, no extension point, and no `virtual` for a
  scenario that does not exist yet.
- Stringly-typed state where an enum or record belongs.
- `#region`, `Manager`/`Helper`/`Utils` grab-bag types, and "Part 2" continuation files.

### G4 — Performance, caching, and object reuse

This tool walks trees of hundreds of thousands of small files. Wall-clock time is dominated by
per-entry overhead, not bytes.

- Enumerate with `EnumerateX`, never `GetX`; do not materialise a tree into a list to count it.
- Bound parallelism explicitly (`MaxDegreeOfParallelism`); never unbounded `Task.Run` fan-out.
- Cache anything derived from a subprocess or the filesystem for the life of the operation —
  resolved tool paths, cache locations, measured sizes. Ask npm where its cache is once.
- Pass `CancellationToken` down every async path. A scan the user cannot abandon is a bug.
- Prefer `IReadOnlyList<T>` over re-enumerable `IEnumerable<T>` for anything consumed twice.

### G5 — Do not recreate objects unnecessarily

- Stateless collaborators are singletons (`ProcessRunner.Default`, `UserEnvironment.Current`),
  injected once through the constructor — never constructed per call.
- Compiled regexes, `SearchValues`, comparers, and lookup sets are `static readonly`.
- Do not re-measure a directory that was measured during planning; carry the number forward.
- Records are for values. Do not clone one to change a field that should have been mutable state
  on a different type.

### G6 — Work in a git worktree

Multiple agents may be working this repository concurrently. Do not commit feature work directly
on `main` from the primary checkout.

```
git worktree add ../Deguffer-<topic> -b feature/<topic>
```

Work there, commit there, and merge back deliberately. Small, focused commits — one gate-abiding
change each, with the *why* in the message.

### G7 — Use sub-agents where applicable

Fan-out work belongs in sub-agents: auditing the codebase against these gates, sweeping for a
pattern across providers, researching an API. Keep the synthesis and the final judgement in the
main thread — a sub-agent reports, it does not decide.

## Build and test

```
dotnet build Deguffer.sln
dotnet test  Deguffer.sln
```

`Deguffer.Core` and `Deguffer.Core.Tests` target `net10.0-windows10.0.19041.0` and build anywhere
with `EnableWindowsTargeting`. `Deguffer.App` needs the Windows App SDK.

## Safety rules that are also code rules

From the spec, restated here because they are the things most easily lost in a refactor:

- **§5.1** Prefer a tool's own eviction command over deleting paths.
- **§5.2** Never target a tool's root directory. Recognised children only; unrecognised is Tier 4.
- **§5.6** Every execution verifies the negative — that protected paths survived.
- **§6.3** Every filesystem path goes through `LongPath`. A `MAX_PATH` truncation is a silent
  partial deletion.
- **§6.5** The Acrylic backdrop is decoration. The UI must be fully legible without it.
