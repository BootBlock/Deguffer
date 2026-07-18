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

## Agent attribution on GitHub content (mandatory)

Anything **you** post or edit on GitHub on the maintainer's behalf must carry an attribution
trailer disclosing that an agent wrote it for @BootBlock. This applies to **every** GitHub issue
and pull-request **comment** *and* every issue/PR **description or body** you author or edit — not
just issues you action end-to-end.

Append it as the **last lines**, after a `---` rule, wording the verb to match what you did:

```markdown
---
This <issue|pull request> was <actioned|opened|updated> by an agent on behalf of @BootBlock.
```

- **Comment on an issue you actioned end-to-end** → keep the exact wording the issue workflow
  uses: `This issue was actioned by an agent on behalf of @BootBlock.`
- **Issue/PR you opened** → use `opened`; a **body you edited** → `updated`; a **pull request**
  → `pull request` in place of `issue`.

The only time to omit it is when GitHub gives you no body to sign (e.g. adding a label). If in
doubt, include it. This does **not** apply to git commit messages — those carry the
`Co-Authored-By` trailer instead.

## Actioning a GitHub issue (workflow)

When the maintainer gives you a Deguffer issue URL —
`https://github.com/BootBlock/Deguffer/issues/<id>` — with no other instruction, treat it as a
request to **action that issue end-to-end** using the workflow below. (Bare `#<id>` or "issue
<id>" in the Deguffer context means the same.) If the message clearly wants only discussion —
"what do you think of…", "should we…", "explain #<id>" — answer instead; when in doubt, ask.

The structural steps here (worktree, code review, merge mechanics) are **internal process**. They
must **never** leak into anything world-readable — not the issue comment, commit messages, branch
names, or code. Someone reading the issue should see only *what* changed and *why*, never the
plumbing that produced it.

**The workflow, in order:**

1. **Read the issue.** `gh issue view <id> --repo BootBlock/Deguffer --json title,body,labels,comments`.
   Understand what's actually being asked; locate the relevant code before changing anything. If it
   touches behaviour, re-read the governing section of `docs/todo/_spec.md` — the spec outranks the
   issue's phrasing.
2. **Work in a git worktree — always.** Per **G6**, and required even though the issue itself won't
   say so. Edit via worktree-relative absolute paths, never touch another agent's worktree, and
   expect `main` to have advanced while you worked.
3. **Implement the fix under every engineering gate above** — G1–G5 in particular, plus the safety
   rules restated from the spec. A change that lands the issue but breaks a gate is not done.
4. **Verify it works.** `dotnet build Deguffer.sln` and `dotnet test Deguffer.sln`. A change to the
   safety model, a provider, or the planner needs a test that would fail without it — especially
   the §5.6 negative check. Where the change has a runtime surface, drive it (the `verify` skill)
   rather than trusting a green build.
5. **Code review before committing.** Run `/code-review high` on the diff and **fix every confirmed
   finding** before proceeding. Re-verify after fixing, then commit inside the worktree.
6. **Land it — by default, don't pause for approval.** The maintainer (@BootBlock) has standing
   authorization to land issue fixes: once the change is implemented, verified and review-clean,
   **merge, push and go on to close it** without a separate go-ahead. Only **pause to ask** when
   there is a genuine, specific question about *this* change — a real design or scope fork, a
   destructive or ambiguous choice, or something that can't be completed cleanly. A bare "shall I
   land it?" is **not** such a question: if the only choice on offer is Land / Hold / Drop, just
   **land it**. When you *do* need to ask, use `AskUserQuestion` for that specific decision — not as
   an approval gate.
7. **Landing mechanics:** merge the worktree branch into `main` with `--no-ff`, then
   `git push origin main` so the issue's referenced commits actually exist on GitHub. Then
   `git worktree remove ../Deguffer-<topic>`; leave other agents' worktrees alone.
8. **Comment, then close as completed.** Post a comment (`gh issue comment <id>`) describing *what*
   was done and *why* in plain terms. **Before posting, self-audit the drafted comment — it is
   world-readable and permanent:**

   - **Match your voice to who filed it — check the issue's author.** When the author is
     **@BootBlock**, that's the project's developer and maintainer, not an end user: write
     peer-to-peer. Don't thank them "for the report" and don't explain the feature back to them as
     if introducing it — state plainly what changed and why. For an issue filed by anyone else, a
     brief, neutral acknowledgement is fine. (The attribution trailer stays regardless of who
     filed it.)
   - **No personally identifiable information about anyone — third parties *or* the maintainer.**
     No real private email addresses, real names tied to private accounts, phone numbers, internal
     hostnames or IP addresses. Use the public `@BootBlock` handle and `example.com` placeholders.
     Deguffer's domain makes this sharper than usual: **never paste a real local filesystem path,
     user profile name, or machine name** from a scan or a repro into an issue. Redact to
     `C:\Users\<user>\…`.
   - **No internal development process, strategy, or tooling.** Keep out worktree / code-review /
     branch / merge mechanics, internal test or file-tool names, CI details, and the agent's own
     reasoning. Describe the *what* and *why*, never the plumbing.
   - **High-level, durable public references are fine:** the affected provider or subsystem, a spec
     section (`§5.2`), a commit SHA, or a file link. Prefer these over process detail.
   - **Always append this exact trailer** as the last lines:

     ```markdown
     ---
     This issue was actioned by an agent on behalf of @BootBlock.
     ```

   **Then reconcile the issue's labels before closing.** The labels should describe what the change
   *actually* turned out to be, not what was assumed when it was filed. Using only the repo's
   existing label set, add any that now clearly apply (an area label, or `bug` vs `enhancement` if
   the nature shifted) and remove any that no longer fit —
   `gh issue edit <id> --repo BootBlock/Deguffer --add-label <name> --remove-label <name>`. Don't
   invent new labels as part of closing; if the right label genuinely doesn't exist yet, note it
   rather than forcing a poor fit.

   Then `gh issue close <id> --repo BootBlock/Deguffer --reason completed`.

If any step can't be completed cleanly (the fix is larger than the issue implies, review surfaces
something structural, `main` conflicts non-trivially), stop and surface it rather than forcing the
workflow through — an issue URL authorises *this* workflow, not an unbounded change.

### Multi-line text goes through a file, not inline quoting

Multi-line commit messages, PR bodies, and issue/PR comments must be passed via a **file**, not
inline shell quoting: write the text to a file, then `git commit -F <file>` and
`gh … --body-file <file>`. Inline quoting for multi-line text is error-prone — a wrong here-string
delimiter can silently wrap the whole message in stray characters, and by the time it reaches a
pushed commit or a posted comment it is expensive or impossible to fix cleanly. A file sidesteps
all shell-quoting rules regardless of which shell runs the command.

## Plan docs carry a status (`docs/todo/`)

The plan, backlog and audit documents in [docs/todo](docs/todo) are long-lived, and a **finished**
plan reads exactly like a live one unless it says so. That is how stale guidance gets followed.

Every `.md` under `docs/todo/` opens with a status banner directly after its heading — the
convention `_spec.md` already uses:

```markdown
> **Status:** 🟢 ACTIVE — the founding specification; no code written yet.
```

- **`🟢 ACTIVE`** / **`📘 REFERENCE`** stay in `docs/todo/`; **`✅ COMPLETE`** / **`⛔ SUPERSEDED`**
  move to `docs/todo/done/`.
- **When an effort finishes, flip the banner and `git mv` it into `done/` in the same change.**
  Grep for inbound links first and update them, or the move strands them.
- **Never rewrite a plan doc's history to match current practice.** A past-tense record of what a
  phase actually ran is evidence; restating it to name today's command asserts something that never
  happened. Correct *live instructions*, and let records stand.
