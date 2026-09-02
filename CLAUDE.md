# Deguffer: the working agreement

> **Six rules break the build, the repository, someone else's work, or a user's disk when they
> are missed. Every one of them is easy to miss. Read the section behind a rule before you rely
> on your memory of it.**
>
> - 🔒 **NEVER COMMIT A SECRET, A REAL PATH, OR A REAL NAME.** This repository is public, and
>   Deguffer reads the developer's disk. A committed path is permanent.
> - 🛡️ **NEVER TARGET A TOOL'S ROOT DIRECTORY.** Recognised children only. An unrecognised child
>   is Tier 4.
> - 🌳 **WORK IN A GIT WORKTREE.** Several agents work here at once. A shared checkout loses edits.
> - 🏁 **LAND THE WORK.** A green build is not done. Commit, merge, push, remove the tree.
> - 🎯 **DO THE WHOLE FIX.** Never the quick, narrow, or low-surface one.
> - ✅ **"VERIFIED" MEANS OBSERVED.** A compiler proves the code is well formed. It proves nothing
>   about which directory the tool deletes.

[docs/todo/_spec.md](docs/todo/_spec.md) is the founding specification. It is the source of truth
for the safety model, the audit evidence behind it, and the decided toolchain. Read it before you
change behaviour. This file governs *how* the code gets written. The spec governs *what* it does.
When the two disagree about what to build, the spec wins.

[AGENTS.md](AGENTS.md) is the cross-agent entry point. It indexes every rule below, and a test
fails the build when the two drift apart. A rule added here belongs in that index too.

## Engineering gates (mandatory)

These are gates, not preferences. A change that breaks one is not ready, whether or not it
compiles and passes tests.

### G1: One responsibility per type and per file

One reason to exist per type. One responsibility per file. If a type needs the word "and" to
describe it, it is two types. Apply SOLID where it earns its keep:

- **Single responsibility.** `DirectoryRemover` deletes. It does not decide *what* to delete.
- **Open/closed.** A new cache source is a new `ICleanupProvider`. It is never an edit to a switch.
- **Dependency inversion.** Core depends on `IUserEnvironment`, `IProcessRunner` and
  `IProcessInspector`. It never calls `Environment.GetFolderPath` or `Process.Start` directly.
  This inversion is what makes the safety rules testable with no package manager installed.

The soft ceiling is about 250 lines per file. Crossing it is a prompt to look for the seam, not a
failure in itself. A 500-line file needs a stated reason.

### G2: No god objects

No type that "manages", "handles", or "processes" the application. No service that both decides
policy and performs I/O. `CleanupPlanner` orchestrates providers and holds no cleanup knowledge of
its own. Each provider holds its own rules and no orchestration. Keep that split.

### G3: No AI-trope or junior-engineer code

These are banned:

- Comments that restate the code (`// increment the counter`). A comment explains *why*, or names
  the non-obvious constraint. A spec section reference is usually the best form.
- Ceremonial abstraction: an interface with one implementation and no test seam, a factory that
  only calls `new`, a wrapper type that forwards every member unchanged.
- `catch (Exception)` that swallows or rethrows unchanged. Catch the specific exceptions you
  expect, and say in a comment why you expect them.
- A defensive null check on a value that cannot be null, and re-validation of an argument that was
  validated one frame up.
- Speculative generality. No configuration knob, no extension point, and no `virtual` for a
  scenario that does not exist yet.
- Stringly-typed state where an enum or a record belongs.
- `#region`, a `Manager` / `Helper` / `Utils` grab-bag type, and a "Part 2" continuation file.

### G4: Performance, caching and object reuse

This tool walks trees of hundreds of thousands of small files. Per-entry overhead dominates the
wall-clock time, not bytes.

- Enumerate with `EnumerateX`, never `GetX`. Do not materialise a tree into a list to count it.
- Bound parallelism explicitly with `MaxDegreeOfParallelism`. Never fan out with unbounded
  `Task.Run`.
- Cache anything derived from a subprocess or the filesystem for the life of the operation:
  resolved tool paths, cache locations, measured sizes. Ask npm where its cache is once.
- Pass `CancellationToken` down every async path. A scan the user cannot abandon is a bug.
- Prefer `IReadOnlyList<T>` to a re-enumerable `IEnumerable<T>` for anything consumed twice.

### G5: Do not recreate objects unnecessarily

- A stateless collaborator is a singleton (`ProcessRunner.Default`, `UserEnvironment.Current`),
  injected once through the constructor. Never construct one per call.
- A compiled regex, a `SearchValues`, a comparer and a lookup set are `static readonly`.
- Do not re-measure a directory that planning already measured. Carry the number forward.
- A record is for a value. Do not clone one to change a field that should have been mutable state
  on a different type.

### G6: Work in a git worktree

Several agents may work this repository at once. A checkout has exactly one working tree, one
index and one `HEAD`, so two agents sharing it overwrite each other's edits, stage each other's
files into a commit, and disagree about which branch is checked out. None of that fails loudly.
It surfaces as a diff nobody can account for.

**The rule:** before your first edit, add a worktree beside the repository and work there. The
primary checkout is for reading, reviewing and integrating. It is never for edits.

```
git worktree add ../Deguffer-<topic> -b feature/<topic>
```

- **One worktree, one branch, one task.** Do not adopt a tree another agent is working in, and do
  not run two tasks in one tree. The point of the isolation is that each tree's diff belongs to a
  single change.
- **Edit through worktree-relative absolute paths.** The Bash tool's working directory drifts
  between calls, so a relative path can land in the primary checkout with no visible error.
- **Expect `main` to have advanced** while you worked.
- **Never run `git clean -ffdx`.** A single `-f` is safe: git refuses to descend into a nested
  repository. The second `-f` removes exactly that protection.
- **The worktrees live beside the repository, not inside it.** `../Deguffer-<topic>` keeps them
  clear of the solution globs, the test discovery and the `dotnet build` walk, with no ignore rule
  to maintain. Do not move them under the working tree.
- Small, focused commits. One gate-abiding change each, with the *why* in the message.

### G7: Use sub-agents where they apply

Fan-out work belongs in sub-agents: auditing the codebase against these gates, sweeping for a
pattern across providers, researching an API. Dispatch independent pieces at the same time rather
than working through them in turn. Give each agent enough context to work without re-deriving what
you already know.

Keep the synthesis and the final judgement in the main thread. A sub-agent reports. It does not
decide.

### G8: What "verified" means

A change is **verified** when someone has *observed* its new behaviour, not when the build is
green. A compiler proves the code is well formed. It proves nothing about whether the tool deletes
the right directory. Deguffer's failure mode is silent, irreversible data loss on someone else's
machine, so this gate is stricter than it would be elsewhere.

**The bar, in the order that actually catches bugs:**

- **A behaviour change needs a test that fails without it.** Write the test, watch it fail for the
  right reason, then make it pass. A test written after the fix and green on its first run has
  proved nothing. It may not exercise the new path at all. When you write the tests after the code,
  which is normal for a whole subsystem, prove each one bites by mutating the production code and
  confirming the test fails. Restore and re-run afterwards.
- **A change to what gets deleted needs the negative assertion (§5.6).** Asserting that the target
  was removed is half a test. Assert that the protected paths survived: the tool root, unrecognised
  siblings, anything in Tier 4. A deletion bug that over-reaches passes every positive assertion.
- **A change to tier classification needs the unrecognised case (§5.2).** Test that a child the
  provider does *not* recognise lands in Tier 4, not only that the recognised ones classify
  correctly. The dangerous direction is an unknown thing silently treated as safe.
- **A change that touches path handling needs an assertion on the *form* of the path (§6.3), not a
  long one.** A deep-tree test cannot fail. .NET prepends `\\?\` itself to any path of 260
  characters or more before it calls Win32, so building a tree past `MAX_PATH` and asserting the
  operation succeeded passes identically with `LongPath.Extended` deleted outright. That is not a
  property of this machine: it was measured in a process where `RtlAreLongPathsEnabled` reports 0,
  and the `LongPathsEnabled` registry value changes none of it. Stripping the prefix from each of
  Core's sixteen seams in turn left the whole suite green for twelve of them.

  So assert what discriminates: that the path handed onward carries `\\?\`. Four tests do, and they
  are the whole of §6.3's real coverage — `DirectoryRemover` and `FileRemover` through the
  `IFileSystem` seam, `ChildDirectories` through the children it returns, and `BoundedFileWalk`
  through the paths it visits. `LongPathTests` guards the runtime assumption underneath them all.

  **Where a seam's only output is a size, a date or a boolean, say so rather than writing a test
  that cannot fail.** `ParallelEnumerationScanner`, `DirectoryAge` and the signature checks are in
  that position today. A deep-tree test over them is still worth having as proof the code *reaches*
  nested content, and it must not be described as proving §6.3.
- **Test through the fakes, never against the real machine.** `FakeUserEnvironment` and the
  `IProcessRunner` and `IProcessInspector` seams exist so the safety rules are provable with no
  npm, NuGet or Gradle installed. That is what G1's dependency inversion buys. A test that passes
  only on a machine with the real tool present does not test the safety rule.
- **Where the change has a runtime surface, drive it.** Types and unit tests do not exercise the
  WinUI shell, the preview-first flow, or a real subprocess. Use the
  [`verify` skill](.claude/skills/verify/SKILL.md) and observe the behaviour rather than inferring
  it.

**Run both commands, every time:** `dotnet build Deguffer.sln` *and* `dotnet test Deguffer.sln`. A
build alone is not verification, and neither is a test run whose output you did not read.

**Never make a test pass by weakening it.** Relaxing an assertion, widening a tolerance, or
deleting an inconvenient case to reach green turns a real failure into a permanent blind spot. If a
test is genuinely wrong, say so explicitly and explain why. Do not loosen it quietly.

**Report what actually happened.** If tests failed, say so and show the output. If you skipped a
step, say which and why. "Verified" is a claim about observed behaviour. Do not make it about work
you did not do.

## Work is not done until it has landed (mandatory)

A green gate is not a finished task. A change that has been built, tested, driven and reviewed, and
then left sitting in a worktree, has shipped nothing. `main` does not have it, no other agent can
build on it, and the tree holds its branch hostage. None of that fails loudly. The session ends
reporting success, and the loss surfaces later when someone asks why a fix that was "done" is not
in the app.

**The rule:** the session that does the work also lands it. Commit everything the change touched,
merge the branch into `main`, push, and remove the worktree and its branch, **before** you report
the task complete.

```
# inside ../Deguffer-<topic>, with both commands green
git status --short                  # every ?? line is work too; leave nothing behind
git add -A
git diff --cached                   # the secrets self-audit, on what will actually be committed
git commit -F <message-file>        # a multi-line message goes through a file, not inline quoting

# then from the primary checkout, the one tree that exists for integrating
git merge --no-ff feature/<topic>
git push origin main
git worktree remove ../Deguffer-<topic>
git branch -d feature/<topic>
```

- **Untracked files are the commonest way half a change lands.** A new provider, test or fake that
  nobody `git add`ed looks complete in the worktree, and arrives on `main` missing the file
  everything else references. The build that proved it green ran against the tree that still had
  it. Read `git status --short` before every commit. A `??` line is work, not noise.
- **Committing is not landing, and merging is not pushing.** An unmerged branch is invisible, and
  an unpushed merge means the commits an issue comment cites do not exist on GitHub. All three
  steps are part of the task, and none of them waits to be asked for.
- **`main` may have moved while you worked.** If the merge is not a fast-forward, resolve it *on
  your branch*: merge `main` into the worktree branch, run both commands there again, then merge
  back. What reaches `main` is then a combination somebody actually verified, not one assembled
  during a conflict resolution.
- **Remove the tree and delete the branch together.** `git worktree remove` leaves the branch
  behind, and a pile of merged `feature/*` branches turns `git branch` into a graveyard where
  nobody can tell live work from finished work. Use `git branch -d`, not `-D`. It refuses anything
  unmerged, which is the check you want.
- **`git worktree remove` refusing is information, not an obstacle.** It fails when the tree still
  holds uncommitted or untracked changes, which means the commit step missed something. Go and look
  at what. Never reach for `--force`, which destroys the work the refusal protects.
- **A locked file is a different failure, and it does not look like one.** If anything still holds a
  handle inside the tree, the removal fails partway naming a path rather than refusing. The usual
  culprit is a Deguffer process left running from the `verify` skill, which holds
  `!Distribution\Deguffer.Core.dll` open. Read the message: the refusal above names uncommitted
  work, this one names a path. Stop the process, then finish by hand:

  ```
  rm -rf ../Deguffer-<topic>        # the leftover the failed removal could not delete
  git worktree prune                # clear the stale administrative entry
  git branch -d feature/<topic>
  ```

- **Land only your own tree.** `git worktree list` shows trees other agents are working in right
  now, and from outside their work in progress looks exactly like abandoned work. Leave them alone.
  This is the same rule as never adopting someone else's tree. Before you remove any tree you did
  not create, check its commit timestamps and file modification times.
- **If the work genuinely cannot land, say so in as many words.** A conflict that is not yours to
  resolve, a gate you cannot get green, a decision that needs the maintainer: those are real. Leave
  the worktree in place and **report the work as unlanded**, naming the branch and what blocks it,
  so someone can pick it up. What is banned is silence: reporting a task done while its only copy
  sits in a tree nobody has been told about.

## Do the whole fix, never the cheap one (mandatory)

Every fix arrives with a cheap version attached: the narrow patch on the one provider that reported
the bug, the guard that suppresses the symptom, the special case that satisfies the failing test.
The cheap version is always quicker to write, smaller to review and easier to justify. It is also
why the same defect gets found again a month later wearing a different symptom. On a tool that
deletes directories, it is why a safety hole gets closed on one path and left open on five.

**The rule:** when you decide *how* to fix something, take the correct, complete, root-cause fix.
Never choose an approach because it is quick, easy, or touches fewer files. Fix the cause at the
level it lives. Fix every instance rather than the reported one. Update every call site, test and
document the change implies, and delete what it supersedes.

If one provider mishandles a path, check whether every provider does, and fix the seam rather than
the provider. If a tier classification is wrong for one child, ask what the rule should have been
and correct the rule.

This is **not** a licence for scope creep. Complete is measured against the defect, not against
everything nearby. It is **not** a licence for speculative generality: G3 still bans the
configuration knob nobody asked for, and "complete" means the cause is gone, not that the machinery
is bigger. It is **not** "fix it badly rather than raise it": if the correct fix is genuinely too
large, or needs a decision that is not yours, say so and leave the defect documented. What is
banned is shipping the narrow version and calling it fixed.

## Build and test

```
dotnet build Deguffer.sln
dotnet test  Deguffer.sln
```

`Deguffer.Core` and `Deguffer.Core.Tests` target `net10.0-windows10.0.19041.0` and build anywhere
with `EnableWindowsTargeting`. `Deguffer.App` needs the Windows App SDK.

Both commands, every time. [G8](#g8-what-verified-means) says why a build alone is not verification.
A running Deguffer holds `!Distribution\Deguffer.Core.dll` open, so `dotnet build` fails with
MSB3021 until you stop the process.

## Safety rules that are also code rules

These come from the spec. They are restated here because a refactor loses them most easily.

- **§5.1** Prefer a tool's own eviction command to deleting paths.
- **§5.2** Never target a tool's root directory. Recognised children only. Unrecognised is Tier 4.
- **§5.6** Every execution verifies the negative, that the protected paths survived.
- **§6.3** Every filesystem path goes through `LongPath`. A `MAX_PATH` truncation is a silent
  partial deletion.
- **§6.5** The Acrylic backdrop is decoration. The UI must be fully legible without it.

## No secrets or personal data (mandatory)

This repository is **public** and licensed MIT. A committed secret is a build-breaking error.
Secrets are effectively permanent once pushed, because they live in the history and may be scraped
within seconds, so the only safe rule is never to let one in.

**For most projects the risk is a leaked API key. Here it is a leaked *path*.** Deguffer's whole
domain is reading the developer's disk, so the material that flows through it (scan output, repro
steps, log lines, test fixtures, screenshots) is naturally full of real usernames, machine names and
directory layouts. That material reaches the repository by reflex, not by carelessness, which is
exactly why it needs a rule.

**Never commit any of these, in any tracked file, source, tests, fixtures, docs, comments and
commit messages included:**

- **A real filesystem path from a real machine.** No `C:\Users\<real-name>\...`, no real machine or
  domain names, no real network share paths. Redact to `C:\Users\<user>\...`, or better, use the
  synthetic roots the fakes already provide.
- **Pasted scan or log output.** Provider discovery results, `dotnet nuget locals` output, planner
  dumps and crash logs all carry real paths. Redact before you paste anywhere, an issue comment and
  a commit message included. `.gitignore` does **not** cover scan output or a log file you create ad
  hoc, so keep those outside the working tree entirely.
- **An API key, token, password, private key, certificate or connection string.** Use an obvious
  placeholder such as `<YOUR_API_KEY>` if an example is genuinely needed. Code-signing material
  (`*.pfx`, `*.cer`) is git-ignored. Keep it that way and never force-add it.
- **Real personal data.** No private email addresses, phone numbers, or real names tied to private
  accounts. Use the GitHub `noreply` identity (`BootBlock@users.noreply.github.com`), the public
  `@BootBlock` handle, `example.com` and `*.test` domains, and `localhost`.
- **A screenshot showing any of the above.** A WinUI capture of the preview flow shows the real
  cache paths and the real profile name of whoever took it. Crop it or re-capture against synthetic
  data. Do not ship the real one.

**Test fixtures are synthetic, and the seams exist to make that easy.** `FakeUserEnvironment` and
the `IProcessRunner` and `IProcessInspector` abstractions mean a test never touches a real profile
directory. Use them rather than hard-coding a path that happened to work locally. A fixture path
should be recognisably invented (`C:\Users\testuser\...`), never copied from your machine.

**Before every commit, self-audit the diff.** Run `git diff --cached` and scan for anything that is
credential-shaped, path-shaped or personal. If something is in doubt, leave it out and ask.

**If a secret is ever committed, stop.** Treat it as compromised. It must be rotated or revoked at
the source *and* the history scrubbed. Removing it in a later commit is **not** sufficient. Raise it
immediately rather than continuing quietly.

## Public-repository hygiene (mandatory)

Everything here is world-readable and permanent: code, comments, commit messages, branch names,
docs and history. Write it as though a stranger will read it tomorrow, because they can.

- **Stay professional and neutral.** No profanity, no disparaging remarks, no jokes at anyone's
  expense, and no venting in code, comments or commit messages. No TODO that names or blames a
  person.
- **No internal-only references.** Do not embed a private ticket ID, an internal wiki or chat URL,
  an internal hostname, or infrastructure detail a stranger should not see. Describe the *what* and
  the *why*, not the internal plumbing.
- **Keep agent process out of the repository.** Worktree names, code-review mechanics and the
  agent's own reasoning belong in the conversation, not in a commit message or a code comment.
  Attribution on GitHub bodies is the deliberate exception, and the section below covers it.
- **Dependency and IP hygiene.** Do not paste code from a source with an incompatible or unknown
  licence. Prefer writing it, or a properly attributed, licence-compatible dependency. Vet a new
  NuGet package for popularity, maintenance and licence before adding it, and keep the dependency
  surface minimal. This repository is **MIT**, so do not introduce text implying a different
  licence.
- **Keep the ignore rules tight.** Before you commit a new kind of generated or local file, confirm
  it belongs in the repository. If it is a build artefact, a local cache, or could contain real
  paths, add it to `.gitignore` instead.

## Agent attribution on GitHub content (mandatory)

Anything **you** post or edit on GitHub on the maintainer's behalf must carry a trailer disclosing
that an agent wrote it for @BootBlock. This applies to **every** GitHub issue and pull-request
**comment**, and to every issue or pull-request **description or body** you author or edit. It is
not limited to an issue you action end to end.

Attribution is disclosure, not internal process, so it always stays. That is the one thing that
separates it from the plumbing which must never leak. See
[public-repository hygiene](#public-repository-hygiene-mandatory).

Append it as the **last lines**, after a `---` rule, wording the verb to match what you did:

```markdown
---
This <issue|pull request> was <actioned|opened|updated> by an agent on behalf of @BootBlock.
```

- A **comment on an issue you actioned end to end** keeps the exact wording the issue workflow
  uses: `This issue was actioned by an agent on behalf of @BootBlock.`
- An **issue or pull request you opened** uses `opened`. A **body you edited** uses `updated`. A
  **pull request** uses `pull request` in place of `issue`.

Omit it only when GitHub gives you no body to sign, such as adding a label. If in doubt, include it.
This does **not** apply to a git commit message, which carries the `Co-Authored-By` trailer instead.

## Reconcile an issue's labels whenever you touch it (mandatory)

The labels on an issue are how this repository is navigated: what kind of work it is, where in the
app it lands, whether it touches what gets deleted, and whether anyone can pick it up. They are also
the first thing to rot, and they rot in one direction. An agent adds a label when it opens an issue,
does the work, and leaves `status: in-progress` sitting on something that shipped a week ago. A
stale label is worse than a missing one, because a reader takes it as current.

**The rule:** whenever you open, action, substantively comment on, or close a GitHub issue, and a
pull request likewise, **reconcile its whole label set in the same visit**. Reconciling is not
"add a label". It is making the set true, which means **removing what no longer applies** as much as
adding what now does.

Choose only from the labels the repository actually has, and read them rather than recalling them:

```
gh label list --repo BootBlock/Deguffer --limit 200
gh issue view <n> --repo BootBlock/Deguffer --json labels -q '.labels[].name'
gh issue edit <n> --repo BootBlock/Deguffer \
  --add-label "type: bug,area: core,safety" --remove-label "status: triage"
```

The taxonomy is five prefixed families plus five standalone modifiers:

| Family | How many | Reconciled means |
| --- | --- | --- |
| `type:` | one or more | Every kind of work the issue actually contains. A crash fix that also rewrites a label carries both `type: bug` and `type: content`. |
| `area:` | one or more | Every part of the codebase the work touches: `core`, `app`, `scanner`, `providers`, `long-paths`, `build`, `tests`, `docs`, `ci`. |
| `status:` | **exactly one, or none** | The only label that *moves*, so the only one that reliably goes stale: `triage`, then `ready`, then `in-progress`, then `needs-review`, and then off entirely when the issue closes. Remove the old one in the same edit that adds the new one. Never leave two. |
| `effort:` | one, once you can judge it | `small`, `medium`, `large` or `epic`, **calibrated to agent wall-clock, not human days**. An `epic` exceeds one session or one context window, so it is a signal to split the issue rather than start it. |
| `priority:` | at most one | Only where it carries information. Most issues need none. `critical` is reserved for a broken app, a leaked secret, or a deletion bug that can reach a user's data. |

| Modifier | When it applies |
| --- | --- |
| `safety` | The change touches what gets deleted, the tier model, or the §5.6 negative assertion. Deguffer's failure mode is irreversible data loss, so this label is how those changes stay findable. Apply it generously. |
| `test-coverage` | The gap is in what the suite can *prove*, not a defect in shipped behaviour. Distinct from `type: test`, which is work on the suite itself. |
| `breaking change` | The change alters stored preferences, a persisted format, or established behaviour a user relies on. |
| `good first issue` | Genuinely self-contained, and its description is enough to start from. |
| `help wanted` | Extra hands or outside expertise would be welcome. |

**Never create a label as a side effect of working an issue.** If nothing in the list fits, say so
in a comment and propose the label. Adding one changes the taxonomy for every issue in the
repository, which is a decision, not a step. Equally, do not force a poor fit. An issue with no
honest `effort:` yet is better than a guessed one.

A label-only edit is the one GitHub write with no body to sign, so it carries no attribution
trailer. If you also comment, that comment does. See
[agent attribution](#agent-attribution-on-github-content-mandatory).

## Close the issue you actioned (mandatory)

The open issues are the queue. An issue whose work has shipped but which nobody closed is
indistinguishable, from the list, from work still waiting to be done. It gets re-triaged,
re-estimated, and eventually picked up by someone who does the whole thing again. It also makes
every count drawn from that list wrong. The agent that actioned the issue is the only one who knows
it is finished, and the moment that knowledge exists is the moment to record it.

**The rule:** when you have actioned an issue and
[the work has landed](#work-is-not-done-until-it-has-landed-mandatory), close it in the same visit,
with a comment saying what was done. Closing an issue whose work still sits in a worktree is worse
than leaving it open, because it asserts something untrue. Land first, then close.

```
gh issue comment <n> --repo BootBlock/Deguffer --body-file <file>
gh issue edit <n> --repo BootBlock/Deguffer --remove-label "status: in-progress"
gh issue close <n> --repo BootBlock/Deguffer --reason completed
```

Closing is a label event too. `status:` comes **off entirely** when the issue closes, and the rest
of the set gets reconciled in the same visit. See
[reconcile an issue's labels](#reconcile-an-issues-labels-whenever-you-touch-it-mandatory).

**Not closing is the exception, and you have to argue it in the comment.** If there is a genuinely
good reason to leave an issue open, **say what it is in the comment you just posted**. An issue left
open with no explanation reads as forgotten, which is the failure this rule exists to stop. These
reasons qualify:

- **You actioned part of it.** The issue asks for four things and you did two. Say which two landed
  and which two remain, or split the remainder into its own issue and close this one against it.
- **It is a tracking or `epic` issue** whose children are still open.
- **It needs a decision, or a verification, that is not yours to make.** A maintainer's call on
  behaviour, or a check on hardware or an elevation you do not have.

These do **not** qualify: leaving it open "for visibility", leaving it for the maintainer to close,
or hedging because you are unsure the fix works. The last one is a verification problem, not a
closing problem. Run both commands, drive the app, then close it.

**A comment on a closed issue does not reopen it.** Follow-up arriving on something already actioned
is normal, and it is how the record of a piece of work stays in one place. Do the extra work, land
it, and add a **new comment** on that same issue describing what you did. A new comment, not an edit
of the old one, because editing rewrites the record rather than extending it. The issue stays closed.

Reopening claims the work is outstanding again, and everyone reading the list will act on that
claim. So it takes a **very good** reason, and there are essentially two: the fix did not work or
regressed, so what was closed was not actually done; or the issue was closed on a false premise,
because the wrong thing was fixed or the report was misread. If the follow-up is genuinely *new*
work rather than a continuation, open a new issue and link it to the closed one. That keeps the
closed issue's record honest about what it covered. When you do reopen, say why in a comment in the
same visit, and put a `status:` label back on. A reopened issue with no status is invisible again.

## Actioning a GitHub issue (workflow)

When the maintainer gives you a Deguffer issue URL,
`https://github.com/BootBlock/Deguffer/issues/<id>`, with no other instruction, treat it as a
request to **action that issue end to end** using the workflow below. A bare `#<id>` or "issue
<id>" in the Deguffer context means the same. If the message clearly wants only discussion ("what
do you think of...", "should we...", "explain #<id>"), answer instead. When in doubt, ask.

The structural steps here, worktree, code review and merge mechanics, are **internal process**. They
must **never** leak into anything world-readable: not the issue comment, the commit messages, the
branch names, or the code. Someone reading the issue should see only *what* changed and *why*, never
the plumbing that produced it. This is
[public-repository hygiene](#public-repository-hygiene-mandatory) applied to issue handling.

**The workflow, in order:**

1. **Read the issue.**
   `gh issue view <id> --repo BootBlock/Deguffer --json title,body,labels,comments,author`.
   Understand what is actually being asked, and locate the relevant code before you change
   anything. If it touches behaviour, re-read the governing section of
   [docs/todo/_spec.md](docs/todo/_spec.md). The spec outranks the issue's phrasing.
2. **Set `status:` before you start.** Put `status: in-progress` on the issue, removing whatever
   status it carried, so a second agent does not pick up the same work. Add an `effort:` label once
   the scope is clear.
3. **Work in a git worktree, always.** [G6](#g6-work-in-a-git-worktree) requires it, and the issue
   itself will not say so.
4. **Implement the fix under every engineering gate**, G1 to G5 in particular, plus the safety rules
   restated from the spec, and
   [do the whole fix](#do-the-whole-fix-never-the-cheap-one-mandatory). A change that lands the
   issue but breaks a gate is not done.
5. **Verify it to the [G8](#g8-what-verified-means) bar:** the failing-first test, the §5.6 negative
   assertion, the §5.2 unrecognised case, an assertion on the *form* of a path where paths are
   touched, the fakes rather than
   the real machine, and the runtime surface actually driven. An issue is not fixed because the
   build is green.
6. **Review before committing.** Run the
   [`/auto-review high`](.claude/skills/auto-review/SKILL.md) skill on the diff and **fix every
   confirmed finding** before you go on. Re-verify after fixing, then commit inside the worktree.
   `/auto-review` is the mandated gate here because it is model-invocable. A maintainer-run
   `/code-review high` is the stronger pass when it is available, so use this one to catch what that
   gate would otherwise find, not to replace it.
7. **Land it. By default, do not pause for approval.** The maintainer (@BootBlock) has standing
   authorization to land issue fixes. Once the change is implemented, verified and review-clean,
   merge, push and go on to close it without a separate go-ahead. Only **pause to ask** when there
   is a genuine, specific question about *this* change: a real design or scope fork, a destructive or
   ambiguous choice, or something that cannot be completed cleanly. A bare "shall I land it?" is
   **not** such a question. If the only choice on offer is land, hold or drop, **land it**. When you
   do need to ask, use `AskUserQuestion` for that specific decision, not as an approval gate.
8. **Landing mechanics** follow
   [work is not done until it has landed](#work-is-not-done-until-it-has-landed-mandatory): merge
   into `main` with `--no-ff`, `git push origin main` so the commits the issue cites actually exist
   on GitHub, then remove the worktree and delete the branch. Leave other agents' worktrees alone.
9. **Comment, reconcile the labels, then close as completed**, per
   [close the issue you actioned](#close-the-issue-you-actioned-mandatory). Post a comment
   describing *what* was done and *why* in plain terms. **Before posting, self-audit the drafted
   comment. It is world-readable and permanent:**

   - **Match your voice to who filed it, so check the issue's author.** When the author is
     **@BootBlock**, that is the project's developer and maintainer, not an end user, so write peer
     to peer. Do not thank them "for the report", and do not explain the feature back to them as
     though introducing it. State plainly what changed and why. For an issue filed by anyone else, a
     brief, neutral acknowledgement is fine. The attribution trailer stays whoever filed it.
   - **No secrets, real paths or personal data.**
     [No secrets or personal data](#no-secrets-or-personal-data-mandatory) applies to the comment
     exactly as it applies to the tree. The trap specific to closing an issue is pasting a repro or a
     scan result verbatim to show the fix working. Redact every path to `C:\Users\<user>\...` first,
     and check any attached screenshot for the same.
   - **No internal development process, strategy or tooling.** Keep out worktree, code-review,
     branch and merge mechanics, internal test or file-tool names, CI details, and the agent's own
     reasoning.
   - **High-level, durable public references are fine:** the affected provider or subsystem, a spec
     section such as `§5.2`, a commit SHA, or a file link. Prefer these to process detail.
   - **Always append this exact trailer** as the last lines:

     ```markdown
     ---
     This issue was actioned by an agent on behalf of @BootBlock.
     ```

If any step cannot be completed cleanly, because the fix is larger than the issue implies, the
review surfaces something structural, or `main` conflicts non-trivially, stop and raise it rather
than forcing the workflow through. An issue URL authorises *this* workflow, not an unbounded change.

### Multi-line text goes through a file, not inline quoting

A multi-line commit message, pull-request body, or issue or pull-request comment goes through a
**file**, never inline shell quoting. Write the text to a file, then use `git commit -F <file>` and
`gh ... --body-file <file>`.

Inline quoting for multi-line text is error-prone. A wrong here-string delimiter can silently wrap
the whole message in stray characters, and by the time it reaches a pushed commit or a posted
comment it is expensive or impossible to fix cleanly. The Bash tool is POSIX `sh`, so a PowerShell
here-string (`@'...'@`) passed to it is taken literally and leaves a stray `@` at each end. A file
sidesteps every shell-quoting rule, whichever shell runs the command.

## Plan docs carry a status (`docs/todo/`)

The plan, backlog and audit documents in [docs/todo](docs/todo) are long-lived, and a **finished**
plan reads exactly like a live one unless it says otherwise. That is how stale guidance gets
followed.

Every `.md` under `docs/todo/` opens with a status banner directly after its heading, the convention
[_spec.md](docs/todo/_spec.md) already uses:

```markdown
> **Status:** 🟢 ACTIVE, the founding specification; no code written yet.
```

- **`🟢 ACTIVE`** and **`📘 REFERENCE`** stay in `docs/todo/`. **`✅ COMPLETE`** and
  **`⛔ SUPERSEDED`** move to `docs/todo/done/`.
- **When an effort finishes, flip the banner and `git mv` the file into `done/` in the same
  change.** Grep for inbound links first and update them, or the move strands them.
- **Never rewrite a plan doc's history to match current practice.** A past-tense record of what a
  phase actually ran is evidence. Restating it to name today's command asserts something that never
  happened. Correct *live instructions*, and let records stand.
