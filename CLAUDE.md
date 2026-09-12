# Deguffer: the working agreement

Deguffer deletes directories on other people's machines, so its failure mode is silent, irreversible
data loss. [docs/todo/_spec.md](docs/todo/_spec.md) decides *what* to build and wins any disagreement
with this file, which decides *how*. Read the governing spec section before you change behaviour.
Every rule here is mandatory. Where one names a *memory note*, open the note before that kind of work.

## Safety rules that are also code rules

A refactor loses these most easily.

- **§5.1** Prefer a tool's own eviction command to deleting paths.
- **§5.2** Never target a tool's root directory. Recognised children only. Unrecognised is Tier 4.
- **§5.6** Every execution verifies the negative: the protected paths survived.
- **§6.3** Every filesystem path goes through `LongPath`. A `MAX_PATH` truncation is a silent partial
  deletion.
- **§6.5** The Acrylic backdrop is decoration. The UI must be fully legible without it.

## G6: work in a git worktree

Several agents work here at once, and a shared checkout loses edits without any error. Before your
first edit, run `git worktree add ../Deguffer-<topic> -b feature/<topic>` and work only there. The
primary checkout is for reading and integrating.

- One worktree, one branch, one task. Never adopt or remove another agent's tree.
- Edit through absolute paths inside your tree. The Bash tool's working directory drifts.
- Keep trees beside the repository, never inside it, where builds and test discovery would see them.
- Never run `git clean -ffdx`. The second `-f` lets git descend into a nested repository.

## G1: one responsibility per type and per file

A type that needs "and" to describe it is two types. A new cache source is a new `ICleanupProvider`,
never an edit to a switch. Core depends on `IUserEnvironment`, `IProcessRunner` and
`IProcessInspector`, never on `Environment.GetFolderPath` or `Process.Start`: that inversion is what
makes the safety rules testable. Past about 250 lines, look for the seam. A 500-line file needs a
stated reason.

## G2: no god objects

No type that "manages", "handles" or "processes" the application, and nothing that both decides
policy and performs I/O. `CleanupPlanner` orchestrates providers and holds no cleanup knowledge. Each
provider holds its own rules and no orchestration.

## G3: no AI-trope or junior-engineer code

- No comment that restates the code. Explain *why*, ideally with a spec section.
- No interface with one implementation and no test seam, factory that only calls `new`, or wrapper
  that forwards every member.
- No `catch (Exception)`. Catch the specific exceptions you expect, and say why you expect them.
- No null check on a value that cannot be null, and no re-validation one frame down.
- No knob, extension point or `virtual` for a scenario that does not exist yet.
- No stringly-typed state where an enum or a record belongs.
- No `#region`, `Manager`/`Helper`/`Utils` type, or "Part 2" file.

## G4: performance, caching and object reuse

Per-entry overhead dominates on trees of hundreds of thousands of small files.

- Enumerate with `EnumerateX`, never `GetX`. Never materialise a tree to count it.
- Bound parallelism with `MaxDegreeOfParallelism`. Never fan out with unbounded `Task.Run`.
- Cache anything derived from a subprocess or the filesystem for the life of the operation.
- Pass a `CancellationToken` down every async path.
- Use `IReadOnlyList<T>` for anything consumed twice.

## G5: do not recreate objects unnecessarily

A stateless collaborator is a singleton (`ProcessRunner.Default`, `UserEnvironment.Current`) injected
once. A compiled regex, `SearchValues`, comparer or lookup set is `static readonly`. Never re-measure
what planning already measured.

## G7: use sub-agents where they apply

Send fan-out work (gate audits, sweeps across providers, API research) to parallel sub-agents, with
the context they need. Keep the synthesis and the final judgement in the main thread.

## G8: what "verified" means

Verified means observed, not compiled. Run `dotnet build Deguffer.sln` *and*
`dotnet test Deguffer.sln` every time, and read the output.

- A behaviour change needs a test that fails without it. Prove a test written after the code bites
  by mutating the code. Memory note: *Deguffer verify by mutation*.
- A change to what gets deleted asserts the §5.6 negative. A tier change tests the unrecognised case.
- A path change asserts the `\\?\` form, not a deep tree. Memory note: *Test Deguffer long-path
  handling by the form of the path*.
- Test through `FakeUserEnvironment` and the process seams, never the real machine.
- Drive a runtime surface with the [`verify` skill](.claude/skills/verify/SKILL.md).
- Never weaken a test to reach green. Report failures and skipped steps as they happened.

## Build and test

Build `Deguffer.App` through PowerShell, not Bash. Memory note: *Build the App through PowerShell,
not Bash*. A running Deguffer holds `!Distribution\Deguffer.Core.dll` open, so stop it before you
build (MSB3021).

## Do the whole fix, never the cheap one

Take the root-cause fix, never the approach that is quick or touches fewer files. Fix every instance
at the level the cause lives: if one provider mishandles a path, fix the seam. Update every call
site, test and document it implies, and delete what it supersedes. Complete is measured against the
defect, not everything nearby. If the right fix is too large or needs someone else's decision, say so.

## Work is not done until it has landed

The session that does the work lands it before it reports done, without being asked.

```
git status --short                  # every ?? line is work too
git add -A && git diff --cached     # the secrets self-audit, on what will be committed
git commit -F <message-file>        # multi-line text goes through a file, gh bodies too
# then from the primary checkout
git merge --no-ff feature/<topic> && git push origin main
git worktree remove ../Deguffer-<topic> && git branch -d feature/<topic>
```

- If `main` moved, merge it into your branch and run both commands there before merging back.
- A removal that refuses has found uncommitted work. Look at it, and never use `--force`. Memory
  note: *A Deguffer worktree removal that fails naming a path hit a held file*.
- If the work cannot land, leave the tree and say so, naming the branch and the blocker.

## No secrets or personal data

This repository is public, and a pushed commit is permanent. The usual leak here is a *path*: scan
output, logs, fixtures and screenshots are full of real usernames, machine names and layouts.

- Never commit a real path, machine, domain or share name. Write `C:\Users\<user>\...`, or use the
  fakes' invented roots such as `C:\Users\testuser\...`.
- Redact scan and log output before you paste it anywhere, a commit message or an issue included.
- Never commit a key, token, password, certificate or connection string, or force-add `*.pfx`.
- Use `BootBlock@users.noreply.github.com`, `@BootBlock`, `example.com`, `*.test` and `localhost`.
- Read `git diff --cached` before every commit. If in doubt, leave it out and ask.
- If a secret is committed, stop and report it. It must be revoked and scrubbed from history.

## Public-repository hygiene

Code, comments, commit messages, branch names and history are world-readable.

- Stay professional and neutral. No TODO names or blames a person.
- No internal references: private ticket IDs, internal URLs or hosts, or agent process such as
  worktrees, review mechanics and reasoning. Describe what changed and why.
- The licence is MIT. Copy no code of unknown or incompatible licence, vet a new NuGet package's
  licence and maintenance, and keep dependencies few.
- Add a new kind of generated or local file to `.gitignore` rather than committing it.

## Actioning a GitHub issue

A Deguffer issue URL, `#<id>` or "issue <id>" with no other instruction asks you to action it end to
end, landing and closing it with no pause for approval. A message that only wants discussion gets an
answer. Memory notes: *Actioning a Deguffer issue end to end*, *Reconcile a Deguffer issue's labels*,
*Close a Deguffer issue once its work has landed*.

Every issue or pull-request body or comment you write ends with this, worded `actioned`, `opened` or
`updated`, with `pull request` for a PR. A commit message carries `Co-Authored-By` instead.

```markdown
---
This issue was actioned by an agent on behalf of @BootBlock.
```

## Plan docs carry a status

Every `.md` under `docs/todo/` opens with a `> **Status:**` banner, and a finished one moves to
`docs/todo/done/` in the same change. Memory note: *Deguffer plan docs carry a status banner*.

## Keep this file small

This file loads into every session. It holds only rules that apply to every change, each in a few
lines. Put detail for one kind of change (a recipe, a cause, a table, an incident) in a memory note
that a short rule names. Shorten or replace a rule before you add one, and never append an
explanation. [AGENTS.md](AGENTS.md) stays a pointer to this file. `AgentGuideBudgetTests` fails when
this file passes 10,000 characters, a section passes 1,200, or `AGENTS.md` passes 600. Never raise a
budget without asking the maintainer.
