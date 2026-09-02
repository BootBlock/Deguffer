# Agent instructions

This file is the cross-agent (AGENTS.md) entry point. The full working agreement lives in
[CLAUDE.md](CLAUDE.md). **Read it before you make a change.** This file is deliberately a pointer,
not a copy. It reproduces in full only the rules whose cost of being missed is unrecoverable, and
links the rest.

Deguffer is a Windows tool that finds and reclaims developer cache directories. It reads the
developer's disk and it deletes directories, so its failure mode is silent, irreversible data loss
on someone else's machine. Every rule below exists because of that.

The **specification** is [docs/todo/_spec.md](docs/todo/_spec.md): the safety model, the audit
evidence behind it, and the decided toolchain. When the spec and this file disagree about *what*
to build, the spec wins.

## Mandatory rules: the complete list

Every rule below is mandatory. The ones marked with an emoji appear in full on this page. The rest
are one click away and are **equally binding**. "I only read AGENTS.md" is not a defence.

| Rule | Where |
| --- | --- |
| The engineering gates, G1 to G8 | [CLAUDE.md](CLAUDE.md#engineering-gates-mandatory) |
| G1: one responsibility per type and per file | [CLAUDE.md](CLAUDE.md#g1-one-responsibility-per-type-and-per-file) |
| G2: no god objects | [CLAUDE.md](CLAUDE.md#g2-no-god-objects) |
| G3: no AI-trope or junior-engineer code | [CLAUDE.md](CLAUDE.md#g3-no-ai-trope-or-junior-engineer-code) |
| G4: performance, caching and object reuse | [CLAUDE.md](CLAUDE.md#g4-performance-caching-and-object-reuse) |
| G5: do not recreate objects unnecessarily | [CLAUDE.md](CLAUDE.md#g5-do-not-recreate-objects-unnecessarily) |
| G6: work in a git worktree, before your first edit | 🌳 below |
| G7: use sub-agents where they apply | [CLAUDE.md](CLAUDE.md#g7-use-sub-agents-where-they-apply) |
| G8: "verified" means observed, not compiled | ✅ below |
| Work is not done until it has landed | 🏁 below |
| Do the whole fix, never the cheap one | 🎯 below |
| Build and test: both commands, every time | [CLAUDE.md](CLAUDE.md#build-and-test) |
| The safety rules that are also code rules | 🛡️ below |
| No secrets or personal data | 🔒 below |
| Public-repository hygiene | 🌐 below |
| Attribution on GitHub issues and pull requests you write | ✍️ below |
| Reconcile an issue's labels whenever you touch it | [CLAUDE.md](CLAUDE.md#reconcile-an-issues-labels-whenever-you-touch-it-mandatory) |
| Close the issue you actioned; a comment on a closed one does not reopen it | [CLAUDE.md](CLAUDE.md#close-the-issue-you-actioned-mandatory) |
| The GitHub issue workflow, end to end | [CLAUDE.md](CLAUDE.md#actioning-a-github-issue-workflow) |
| Multi-line text goes through a file, not inline quoting | [CLAUDE.md](CLAUDE.md#multi-line-text-goes-through-a-file-not-inline-quoting) |
| Plan docs under `docs/todo/` carry a status banner | [CLAUDE.md](CLAUDE.md#plan-docs-carry-a-status-docstodo) |

**Adding a rule to CLAUDE.md? It belongs in that table too.** `AgentGuideParityTests` fails the
build when a CLAUDE.md section is missing from this index, or when a link here points at a heading
that no longer exists. This page fell behind once in the reference projects, and drift is not
something review reliably catches.

## 🌳 G6: work in a git worktree (mandatory)

**This rule gates your first action, which is why it is here rather than only linked.**

Several agents may work this repository at once. A checkout has exactly one working tree, one index
and one `HEAD`, so two agents sharing it overwrite each other's edits, stage each other's files into
a commit, and disagree about which branch is checked out. None of that fails loudly. It surfaces as
a diff nobody can account for.

**The rule:** before your first edit, add a worktree beside the repository and work there. The
primary checkout is for reading, reviewing and integrating. It is never for edits.

```
git worktree add ../Deguffer-<topic> -b feature/<topic>
```

One worktree, one branch, one task. Do not adopt a tree another agent is working in, and never
switch the primary checkout's branch to do work. Edit through worktree-relative absolute paths: the
Bash tool's working directory drifts between calls, so a relative path can land in the primary
checkout with no visible error. Expect `main` to have advanced while you worked. **Never run
`git clean -ffdx`**: a single `-f` is safe because git refuses to descend into a nested repository,
and the second `-f` removes exactly that protection. The worktrees live *beside* the repository so
that the solution globs, the test discovery and the `dotnet build` walk never see them. Full detail
in [CLAUDE.md](CLAUDE.md#g6-work-in-a-git-worktree).

## 🏁 Work is not done until it has landed (mandatory)

A green build is not a finished task. A change left sitting in a worktree has shipped nothing:
`main` does not have it, no other agent can build on it, and the tree holds its branch hostage. The
session ends reporting success and the loss surfaces days later.

**The rule:** the session that does the work also lands it, **before** it reports the task complete.

```
git status --short                  # every ?? line is work too; leave nothing behind
git add -A && git diff --cached     # then the secrets self-audit on the staged diff
git commit -F <message-file>        # a multi-line message goes through a file
git merge --no-ff feature/<topic>   # from the primary checkout
git push origin main
git worktree remove ../Deguffer-<topic>
git branch -d feature/<topic>
```

Untracked files are the commonest way half a change lands. Committing is not landing, and merging
is not pushing: an unmerged branch is invisible, and an unpushed merge means the commits an issue
comment cites do not exist on GitHub. If `git worktree remove` **refuses**, the commit step missed
something, so go and look. Never use `--force`. If it instead **fails naming a path**, something
still holds a handle inside the tree, usually a Deguffer process left running from the `verify`
skill: stop it, then `rm -rf` the leftover and `git worktree prune`. Land only your own tree, and
check commit timestamps before removing one you did not create. If the work genuinely cannot land,
leave the tree and **say so explicitly**, naming the branch and the blocker. Silence is the banned
outcome. Full detail in
[CLAUDE.md](CLAUDE.md#work-is-not-done-until-it-has-landed-mandatory).

## 🛡️ The safety rules that are also code rules (mandatory)

Deguffer deletes directories on a real developer's machine. These five come from the spec, and a
refactor loses them most easily.

- **§5.1** Prefer a tool's own eviction command to deleting paths.
- **§5.2** Never target a tool's root directory. Recognised children only. An unrecognised child is
  **Tier 4**. The dangerous direction is an unknown thing silently treated as safe.
- **§5.6** Every execution verifies the negative, that the protected paths survived. Asserting the
  target was removed is half a test.
- **§6.3** Every filesystem path goes through `LongPath`. A `MAX_PATH` truncation is a silent
  partial deletion. Test it by asserting the **form** of the path — a deep-tree test cannot fail,
  because .NET prepends `\\?\` itself past 260 characters.
- **§6.5** The Acrylic backdrop is decoration. The UI must be fully legible without it.

Full detail in [CLAUDE.md](CLAUDE.md#safety-rules-that-are-also-code-rules), and the specification
itself in [docs/todo/_spec.md](docs/todo/_spec.md).

## ✅ G8: "verified" means observed (mandatory)

A change is verified when someone has *observed* its new behaviour, not when the build is green. A
compiler proves the code is well formed. It proves nothing about which directory the tool deletes.

- **A behaviour change needs a test that fails without it.** Watch it fail for the right reason,
  then make it pass. A test written after the fix and green on its first run has proved nothing. If
  you write the tests after the code, prove each one bites by mutating the production code.
- **A change to what gets deleted needs the §5.6 negative assertion.** A change to tier
  classification needs the §5.2 unrecognised case. A change touching path handling needs an
  assertion that the path handed onward carries `\\?\` — a path past `MAX_PATH` proves nothing,
  because the runtime prefixes it for you.
- **Test through the fakes, never against the real machine.** `FakeUserEnvironment` and the
  `IProcessRunner` and `IProcessInspector` seams make the safety rules provable with no npm, NuGet
  or Gradle installed.
- **Where the change has a runtime surface, drive it** with the
  [`verify` skill](.claude/skills/verify/SKILL.md).
- **Run both commands, every time:** `dotnet build Deguffer.sln` *and* `dotnet test Deguffer.sln`.

**Never make a test pass by weakening it**, and **report what actually happened**: if tests failed,
say so and show the output; if you skipped a step, say which and why. Full detail in
[CLAUDE.md](CLAUDE.md#g8-what-verified-means).

## 🔒 No secrets or personal data (mandatory)

This repository is **public** and licensed MIT. A committed secret is a build-breaking error.
Secrets are effectively permanent once pushed, because they live in the history and may be scraped
within seconds, so the only safe rule is never to let one in.

**For most projects the risk is a leaked API key. Here it is a leaked *path*.** Deguffer's whole
domain is reading the developer's disk, so scan output, repro steps, log lines, test fixtures and
screenshots are naturally full of real usernames, machine names and directory layouts. That material
reaches the repository by reflex, not by carelessness.

**Never commit any of these, in any tracked file, source, tests, fixtures, docs, comments and commit
messages included:**

- **A real filesystem path from a real machine.** No `C:\Users\<real-name>\...`, no real machine or
  domain names, no real network share paths. Redact to `C:\Users\<user>\...`, or better, use the
  synthetic roots the fakes already provide.
- **Pasted scan or log output.** Provider discovery results, `dotnet nuget locals` output, planner
  dumps and crash logs all carry real paths. Redact before you paste anywhere, an issue comment and
  a commit message included. `.gitignore` does not cover a log file you create ad hoc, so keep those
  outside the working tree entirely.
- **An API key, token, password, private key, certificate or connection string.** Use an obvious
  placeholder such as `<YOUR_API_KEY>`. Code-signing material (`*.pfx`, `*.cer`) is git-ignored, so
  keep it that way and never force-add it.
- **Real personal data.** Use the GitHub `noreply` identity
  (`BootBlock@users.noreply.github.com`), the public `@BootBlock` handle, `example.com` and `*.test`
  domains, and `localhost`.
- **A screenshot showing any of the above.** A WinUI capture of the preview flow shows the real
  cache paths and the real profile name of whoever took it.

**Test fixtures are synthetic, and the seams exist to make that easy.** A fixture path should be
recognisably invented (`C:\Users\testuser\...`), never copied from your machine.

**Before every commit, self-audit the diff.** Run `git diff --cached` and scan for anything that is
credential-shaped, path-shaped or personal. If something is in doubt, leave it out and ask.

**If a secret is ever committed, stop.** Treat it as compromised. It must be rotated or revoked at
the source *and* the history scrubbed. Removing it in a later commit is **not** sufficient. Raise it
immediately rather than continuing quietly. Full detail in
[CLAUDE.md](CLAUDE.md#no-secrets-or-personal-data-mandatory).

## 🌐 Public-repository hygiene (mandatory)

Everything here is world-readable and permanent: code, comments, commit messages, branch names, docs
and history. Write it as though a stranger will read it tomorrow, because they can.

- **Stay professional and neutral.** No profanity, no disparaging remarks, no jokes at anyone's
  expense, and no venting in code, comments or commit messages. No TODO that names or blames a
  person.
- **No internal-only references.** No private ticket ID, internal wiki or chat URL, internal
  hostname, or infrastructure detail a stranger should not see. Describe the *what* and the *why*,
  not the internal plumbing.
- **Keep agent process out of the repository.** Worktree names, code-review mechanics and the
  agent's own reasoning belong in the conversation, not in a commit message or a code comment.
  Attribution on GitHub bodies is the deliberate exception, and the section below covers it.
- **Dependency and IP hygiene.** Do not paste code from a source with an incompatible or unknown
  licence. Vet a new NuGet package for popularity, maintenance and licence before adding it, and
  keep the dependency surface minimal. This repository is **MIT**, so do not introduce text implying
  a different licence.
- **Keep the ignore rules tight.** If a new kind of file is a build artefact, a local cache, or
  could contain real paths, add it to `.gitignore` instead of committing it.

Full detail in [CLAUDE.md](CLAUDE.md#public-repository-hygiene-mandatory).

## ✍️ Attribution on GitHub content (mandatory)

Anything **you** post or edit on GitHub on the maintainer's behalf must disclose that an agent wrote
it. This covers **every** issue and pull-request **comment**, and every issue or pull-request
**description or body** you author or edit. Attribution is disclosure, not internal process, so it
always stays, unlike the plumbing that must never leak.

Append it as the **last lines**, after a `---` rule, wording the verb to match what you did
(`actioned`, `opened` or `updated`, and `pull request` in place of `issue`):

```markdown
---
This issue was actioned by an agent on behalf of @BootBlock.
```

Omit it only when GitHub gives you no body to sign, such as adding a label. If in doubt, include it.
This does **not** apply to a git commit message, which carries a `Co-Authored-By` trailer instead.
Full detail in [CLAUDE.md](CLAUDE.md#agent-attribution-on-github-content-mandatory).

**The same visit owes the issue its labels.** Whenever you open, action, comment substantively on,
or close an issue or a pull request, reconcile its **whole** label set from the repository's own
list (`gh label list --repo BootBlock/Deguffer --limit 200`), removing what no longer applies as
much as adding what now does, and never inventing a label. The taxonomy is five prefixed families
(`type:`, `area:`, `status:`, `effort:`, `priority:`) plus five modifiers (`safety`,
`test-coverage`, `breaking change`, `good first issue`, `help wanted`). `status:` is the one that
goes stale: exactly one, or none once the issue closes. Apply `safety` generously, because it is how
changes to what gets deleted stay findable. Full detail in
[CLAUDE.md](CLAUDE.md#reconcile-an-issues-labels-whenever-you-touch-it-mandatory).

**And the same visit closes it.** An issue you actioned, whose work has landed, gets closed then and
there. An open issue whose work has shipped is indistinguishable from work still waiting, and
someone will do it again. Comment what you did, then close it. Leaving it open needs a reason, and
the reason goes **in that comment**: you actioned only part of it, it tracks children still open, or
it needs a decision that is not yours. "For visibility" and "the maintainer can close it" do not
count. A **comment on an already-closed issue does not reopen it**: do the extra work and add a
*new* comment. Reopening claims the work is outstanding again, so it takes a very good reason, that
the fix regressed or the issue was closed on a false premise. Genuinely new work gets a new issue
linked to the old one. Full detail in
[CLAUDE.md](CLAUDE.md#close-the-issue-you-actioned-mandatory).

## 🎯 Do the whole fix, never the cheap one (mandatory)

Every fix arrives with a cheap version attached: the narrow patch on the one provider that reported
the bug, the guard that suppresses the symptom, the special case that satisfies the failing test. It
is always quicker to write, smaller to review and easier to justify, and it is why the same defect
gets found again a month later wearing a different symptom. On a tool that deletes directories, it
is why a safety hole gets closed on one path and left open on five.

**The rule:** when you decide *how* to fix something, take the correct, complete, root-cause fix.
Never choose an approach because it is quick, easy, or touches fewer files. Fix the cause at the
level it lives, fix every instance rather than the reported one, update every call site, test and
document the change implies, and delete what it supersedes.

This is **not** a licence for scope creep: complete is measured against the defect, not everything
nearby. It is **not** a licence for speculative generality: G3 still bans the configuration knob
nobody asked for. It is **not** "fix it badly rather than raise it": if the correct fix is genuinely
too large or needs a decision that is not yours, say so and leave the defect documented. What is
banned is shipping the narrow version and calling it fixed. Full detail in
[CLAUDE.md](CLAUDE.md#do-the-whole-fix-never-the-cheap-one-mandatory).
