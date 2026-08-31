---
name: auto-review
description: >-
  Review the current working-tree diff (against main) for correctness bugs, CLAUDE.md gate
  violations, safety-rule breaches, and the structural artefacts machine-written code
  characteristically leaves behind (phantom APIs, half-applied parallel edits, re-implemented
  seams, test theatre, suppressed errors, scope creep), reporting only high-signal findings.
  Model-invocable stand-in for the built-in /code-review, for use before merging or handing off
  work. Accepts an effort argument: low | medium | high (default medium).
argument-hint: "[low|medium|high]"
allowed-tools:
  - Bash(git diff:*)
  - Bash(git status:*)
  - Bash(git merge-base:*)
  - Bash(git log:*)
  - Bash(git rev-parse:*)
  - Bash(git show:*)
  - Agent
  - Task
  - ReportFindings
  - Read
  - Grep
  - Glob
---

# auto-review: agent-invocable working-diff review

This skill is the **mandated review gate** for Deguffer issue work, named at step 6 of
[Actioning a GitHub issue](../../../CLAUDE.md#actioning-a-github-issue-workflow). It reproduces the
bundled reviewer's **find, then validate, then high-signal-only** rubric, adapted to review the
**local working-tree diff** rather than a GitHub pull request, which is the need here: a change is
reviewed *before* it is merged, and before a pull request exists.

> **What it is not.** This is a faithful single-orchestration approximation, not the bundled
> `/code-review`. It does not run that reviewer's cloud or `ultra` multi-agent machinery, and its
> depth depends on the effort you pass. A maintainer-run `/code-review high` remains the stronger
> pass when it is available. Use this to catch what that gate would otherwise find, not to replace
> it.

**Provenance and maintenance.** The rubric is adapted from the public Anthropic source at
`github.com/anthropics/claude-code`, path `plugins/code-review/commands/code-review.md` (the
`code-review@claude-code-plugins` plugin), by way of the sibling Gubbins projects. Drift from the
bundled reviewer is expected and acceptable. **Re-sync this file from that public source whenever
the Claude Code extension is updated:** fetch the latest `code-review.md`, diff it against this
rubric, and fold in the changes, keeping the local adaptations below.

Three things here are **local additions with no upstream counterpart**, and a re-sync must preserve
them rather than overwrite them: the working-tree diff scope, the **safety lane**, and the
**machine-artefact lane** (step 4's third and fourth agent types). The safety lane exists because
Deguffer's failure mode is irreversible data loss, which no general rubric weights correctly. The
machine-artefact lane exists because the upstream bar, "will not compile, is definitely wrong, or
breaks a stated rule", is tuned for human-authored code and is close to orthogonal to how
machine-authored code fails. Machine-written code compiles. It goes wrong by referring to things
that were never written, by changing one of six places that had to change together, by re-solving a
solved problem, and by asserting a completion it has not reached. None of those trip the upstream
bar.

## Agent assumptions (applies to every agent and subagent)

- All tools work. Do not test a tool or make an exploratory call. Say this to every subagent you
  launch.
- Call a tool only when it is required. Every call has a clear purpose.

## Effort

Read the argument (`low`, `medium` or `high`, default `medium`). It scales the review breadth:

- **low**: 3 review agents (1 gate compliance, 1 safety, 1 combined bug/logic and
  machine-artefact). Skip the summary agent and summarise the diff yourself.
- **medium** (default): a summary agent plus 5 review agents (1 gate compliance, 1 safety,
  2 bug/logic, 1 machine-artefact).
- **high**: a summary agent plus 8 review agents (2 gate compliance, 2 safety, 2 bug/logic,
  2 machine-artefact, one taking the *mechanical* checks A to D and one the *intent* checks E to H).

The safety lane never drops below one agent, at any effort, when the diff touches
`Deguffer.Core/**`.

## Steps: follow precisely

1. **Establish the diff scope.** This reviews *local* work, committed and uncommitted, against
   `main`, not a pull request.
   - `BASE=$(git merge-base main HEAD)`.
   - The target is everything from `BASE` to the working tree: `git diff BASE`, which is the full
     delta a merge into `main` would introduce. Use `git diff BASE --stat` for the file list and
     `git diff BASE` for the hunks.
   - If the diff is empty, stop and report: "No changes to review against main."

2. **Collect the relevant CLAUDE.md paths** (paths only, not contents): the root `CLAUDE.md`, plus
   any `CLAUDE.md` in a directory containing a file the diff modifies. When judging a file's
   compliance, consider only the CLAUDE.md files that share its path or a parent of it.

3. **Summarise the changes** (do this inline at `low` effort). Capture the author's intent, inferred
   from the branch name, the commit messages (`git log BASE..HEAD`) and the diff. That intent is
   context every review agent receives. Note which part of `docs/todo/_spec.md` governs the change,
   because the spec outranks the issue's phrasing.

4. **Launch the review agents in parallel**, at the count the effort sets. Give every agent the
   change summary and inferred intent, the diff, and the relevant CLAUDE.md paths. Each returns a
   list of issues, and each issue carries a **description** and the **reason** it was flagged, such
   as "G4 violation", "§5.2 breach", "bug", or the machine-artefact check letter it matched.

   **Gate-compliance agent(s).** Audit the changed code against the applicable CLAUDE.md rules, and
   quote the exact rule broken. The gates worth checking here:

   - **G1, one responsibility.** A file well past the ~250-line ceiling with no stated reason. A
     type that needs "and" to describe it. Core reaching `Environment.GetFolderPath`,
     `Process.Start`, `Process.GetProcessesByName` or `Registry` directly rather than through
     `IUserEnvironment`, `IProcessRunner` or `IProcessInspector`. A new cache source added as an arm
     of an existing switch rather than as a new `ICleanupProvider`.
   - **G2, no god objects.** A type that "manages", "handles" or "processes". A service that both
     decides policy and performs I/O. `CleanupPlanner` gaining cleanup knowledge of its own, or a
     provider gaining orchestration.
   - **G3, no AI-trope code.** A comment restating the code. An interface with one implementation
     and no test seam. A factory that only calls `new`. A wrapper forwarding every member unchanged.
     `catch (Exception)` that swallows or rethrows unchanged. A null check on a value that cannot be
     null, or re-validation of an argument validated one frame up. A configuration knob, extension
     point or `virtual` for a scenario that does not exist. Stringly-typed state where an enum or a
     record belongs. `#region`, a `Manager` / `Helper` / `Utils` type, or a "Part 2" file.
   - **G4, performance.** `GetFiles` / `GetDirectories` / `GetFileSystemEntries` where
     `EnumerateX` belongs. A tree materialised into a list to count it. `Task.Run` fan-out with no
     `MaxDegreeOfParallelism`. A subprocess or filesystem probe repeated where the result should be
     cached for the operation (asking npm where its cache is more than once is the canonical case).
     An async path with no `CancellationToken`. An `IEnumerable<T>` enumerated twice.
   - **G5, object reuse.** `new ProcessRunner()` or `new UserEnvironment()` per call rather than
     `ProcessRunner.Default` and `UserEnvironment.Current` injected once. A `Regex`, `SearchValues`,
     comparer or lookup set constructed per call rather than `static readonly`. A directory
     re-measured after planning already measured it.
   - **No secrets or personal data.** Any real filesystem path, profile name, machine name, or
     pasted scan or log output, in source, a test, a fixture, a comment or a commit message. A
     fixture path must be recognisably invented, such as `C:\Users\testuser\...`.
   - **Public-repository hygiene.** Agent process leaking into a commit message or a code comment:
     a worktree name, review mechanics, or the agent's own reasoning.

   **Safety agent(s).** Deguffer deletes directories on a real machine, so this lane reads the spec
   and the surrounding code rather than the hunk alone. Flag:

   - **§5.1** A path deleted directly where the tool offers its own eviction command.
   - **§5.2** A tool's **root** directory reaching a target list. A recognised-child list widened to
     a wildcard, a prefix match, or "everything except". A classification path where an
     **unrecognised** child can come out as anything but Tier 4. This is the dangerous direction:
     an unknown thing silently treated as safe.
   - **§5.6** A change to what gets deleted whose test asserts only that the target was removed.
     The negative assertion, that the tool root, the unrecognised siblings and anything in Tier 4
     survived, is half the test and the half that catches over-reach.
   - **§6.3** A filesystem path that does not go through `LongPath`. A `MAX_PATH` truncation is a
     silent partial deletion, so a raw `Path.Combine` result handed to a delete or an enumerate is a
     finding.
   - **§6.5** Legibility that depends on the Acrylic backdrop.
   - **Tier drift.** A provider gaining a recognised child with no test proving an unrecognised
     sibling still lands in Tier 4.

   *Evidence:* the spec section, and the `file:line` where the rule is broken. A safety finding
   without a cited section is not a finding.

   **Bug/logic agent(s).** Scan for bugs **visible in the diff itself**, without reading wide
   context: an inverted condition, a wrong operator, an off-by-one, a missing `await`, an unhandled
   null, incorrect logic. Do not flag anything you cannot validate from the hunk.

   **Machine-artefact agent(s).** Work the **A to H checklist** below, reproduced in full in the
   agent's brief. Unlike the bug/logic lane, this one **must read the repository**. `Grep`, `Glob`
   and `Read` are the whole point, because every check here is confirmed or killed by evidence
   outside the hunk.

   **CRITICAL: only HIGH-SIGNAL issues.** Flag an issue only when one of these holds:
   - The code will fail to compile (a syntax or type error, a missing using, an unresolved
     reference). Note that `TreatWarningsAsErrors` is on, so a warning is a build failure.
   - The code will definitely produce a wrong result regardless of input.
   - A clear, unambiguous CLAUDE.md or spec violation whose exact rule you can quote.
   - A machine-artefact check matches **and** you can cite the concrete counter-evidence it demands:
     the `file:line` of the thing that does not exist, the sibling site left un-updated, the existing
     seam that was re-implemented, the assertion that cannot fail. No citation, no finding.

   Do **not** flag code style, an issue that depends on a specific input or state, or a subjective
   suggestion. If you are not certain an issue is real, do not flag it. A false positive erodes
   trust and wastes the reviewer's time.

5. **Validate every flagged issue with a second, independent pass.** For each issue from step 4,
   launch a subagent whose only job is to confirm, with high confidence, that the issue is real in
   *this* code. Give it the summary, the intent and the issue description. This is adversarial:
   default to "not confirmed" when the evidence is thin.

   For a **machine-artefact** finding the validator must *independently re-derive* the cited
   evidence rather than take it on trust: re-run the search for the "missing" symbol including
   partial classes and generated code, open the sibling site claimed to be un-updated, read the seam
   claimed to be re-implemented and confirm it actually covers this case. These findings assert
   *absence*, and absence is the easiest thing to get wrong from a partial grep.

   For a **safety** finding the validator must open `docs/todo/_spec.md` at the cited section and
   confirm it says what the finding claims. A misquoted spec section is worse than no finding.

6. **Filter to validated issues, then de-duplicate.** Discard anything step 5 did not confirm. The
   lanes overlap by design: a new provider registered nowhere is both a G1 violation and a
   half-applied parallel edit. Collapse findings that share a root cause into one, keeping the
   phrasing that names the rule or the evidence most precisely.

7. **Report.**
   - **Report the confirmed findings through the `ReportFindings` tool** if it is available. One
     call, ranked most severe first, an empty array if nothing survived. Do not also print them as
     prose.
   - If `ReportFindings` is not available, print a terminal summary instead: each confirmed issue
     with a one-line description and its `file:line`. If none survived, state exactly: "No issues
     found. Checked for bugs, CLAUDE.md gate compliance, the spec's safety rules, and
     machine-artefact checks A to H."

This skill **reports** findings. It does not edit code. Fix what it surfaces before continuing, then
re-run if the change was substantial.

## Machine-artefact checks (A to H)

Code written by a model fails differently from code written by a tired human. It compiles, it reads
fluently, and it is plausibly shaped, so the bug/logic lane above, which deliberately looks only at
the hunk, is blind to most of it. These failures are **structural**: something the diff asserts
exists does not, something that had to change in six places changed in one, something already solved
got solved again slightly differently. Each check below names the **evidence required** to flag it,
and that requirement is what keeps the lane high-signal rather than a code-quality free-for-all.

**Mechanical checks (A to D), verified by searching the repository.**

- **A. Phantom surface.** The diff references something that does not exist: a method, property,
  type, interface member, resource key, MSBuild property, spec section, or file path. Fluent
  invention is this failure mode's signature. The call reads perfectly and the callee was never
  written. Deguffer-specific instances:
  - A member called on `ICleanupProvider` that the interface does not declare.
  - A XAML `x:Name`, `{Binding}` path, `{StaticResource}` key or converter that has no backing
    property, resource or registration. XAML resolves at runtime, so this compiles and then throws.
  - A **Segoe Fluent Icons glyph** that is not the character the code intends. A PUA glyph reads back
    as invisible or empty through the file tools, so verify it by character code, never by eye.
  - A spec section reference (`§5.2` and the like) that `docs/todo/_spec.md` does not contain.
  - A member taken from a NuGet package that the pinned version does not provide.
  - A preference key read from `PreferenceStore` that nothing ever writes.

  *Evidence:* a search for the symbol that returns nothing, having also checked partial classes and
  generated code. Quote the search and the referencing `file:line`.

- **B. Half-applied parallel edit.** This codebase is full of sites that must change together, and a
  model reliably updates the one it was looking at:
  - A new `ICleanupProvider` implementation that nothing registers where the providers are composed.
    The provider exists, is tested, and never runs.
  - A new `SafetyTier` member with no arm in the badge, the tooltip, the converter or the ordering.
  - A new preference added to the type with no default in `PreferenceStore` and no read at the point
    of use.
  - A provider gaining a recognised child with no matching test that an unrecognised sibling still
    lands in Tier 4.
  - A renamed symbol updated at its definition but not at every call site, including XAML, which the
    compiler does not check.
  - A new cache location added to discovery but not to the size measurement, or the reverse.

  *Evidence:* the sibling site, by `file:line`, that still reflects the old shape.

- **C. Re-implemented seam.** The diff hand-rolls something the repository already owns a canonical
  seam for: a path joined or normalised outside `LongPath`, a `Process.Start` outside
  `IProcessRunner`, an `Environment.GetFolderPath` or `SpecialFolder` lookup outside
  `IUserEnvironment`, a process check outside `IProcessInspector`, a directory walk beside
  `DirectoryScanner`, a second size measurement, a second deletion path beside `DirectoryRemover`.
  The give-away is a *second*, subtly different implementation of a solved problem, and on this
  codebase the second implementation is the one that misses the long-path case.
  *Evidence:* the existing seam's path, plus a one-line statement that it genuinely covers this
  case. If the seam does **not** fit, that is not a finding.

- **D. Dead on arrival.** Code added in this diff that nothing reaches: an exported helper,
  parameter, option or branch with no caller; a flag only ever passed one value; a superseded
  implementation left beside its replacement; an unreachable branch after an early return; a using
  directive nothing needs. This is also where **G3's speculative generality** surfaces: an
  abstraction nobody calls is dead on arrival by definition.
  *Evidence:* a repository-wide search for the identifier showing the definition is its only
  mention.

**Intent checks (E to H), verified against the change summary and the intent from step 3.**

- **E. Test theatre.** A test that cannot fail: asserting on the fake rather than the subject,
  `Assert.True(true)`, an `await` with no assertion after it, a test that would still pass with the
  production change reverted. Deguffer's own recorded instances are worth naming to the agent,
  because all of them were green:
  - A **deletion test with no §5.6 negative assertion**. It proves the target went and proves
    nothing about what else went with it.
  - A **long-path test that does not actually exceed `MAX_PATH`**, or one that passes on a machine
    with `LongPathsEnabled` set regardless of whether `LongPath` handles anything.
  - A **tier test that covers only the recognised children**, so the unrecognised case is untested
    in the one direction that loses data.
  - A test whose fixture makes the assertion vacuous, such as a "do not double-count directories"
    test whose directories have zero-sized data streams.

  Also in scope: **assertions weakened or deleted to reach green**, a specific expectation loosened,
  a case rewritten to match the new and possibly wrong output, or a `Skip` left on a `Fact`.
  *Evidence:* quote the assertion and say why no realistic breakage would trip it.

- **F. Suppression instead of a fix.** An error silenced rather than resolved: a
  `#pragma warning disable`, a `!` null-forgiving operator on something that can genuinely be null, a
  widened nullable annotation, a `catch (Exception)` or a `catch { }` that swallows, a `catch` whose
  only body is a log line so a real failure now passes silently, or a `?? fallback` papering over a
  value that should never have been missing. `TreatWarningsAsErrors` is on, so a suppression here is
  usually someone getting past the build rather than fixing the cause.
  *Evidence:* the suppression's `file:line` and what it hides. A `catch` of a **specific** exception
  with a comment saying why it is expected is exactly what G3 asks for, and is not a finding.

- **G. Scope creep.** Changes outside what the stated intent asked for: a drive-by rename or
  refactor of untouched code, a whole file reformatted around a two-line fix, a speculative
  configuration knob, an unrelated NuGet package added. A new dependency needs a stated reason and a
  licence check.
  *Evidence:* the hunk, plus why the intent from step 3 does not cover it. A call site that genuinely
  had to move is not scope creep.

- **H. Unbacked claim or leftover placeholder.** Prose asserting something the code does not do: a
  comment, a document or a commit message claiming behaviour, coverage or completion the diff does
  not deliver. "Verified" in a commit message for a change nobody drove is the case this project
  cares about most. Plus the artefacts of an unfinished pass: a `TODO` or `FIXME`, a stub returning
  empty, hard-coded sample data on a real path, a `Debug.WriteLine` left in, a commented-out block.
  Also **change-narrating comments** such as `// now uses X instead of Y` or `// Added for the new
  flow`, which describe the *edit* rather than the code and read as stale noise the moment they land.
  Public-repository hygiene forbids one that references the agent or the process that produced it.
  *Evidence:* the claim and the code that contradicts it, or the placeholder's `file:line`.

When reporting through `ReportFindings`, use a `category` slug naming the check: `phantom-api`,
`parallel-edit-drift`, `reimplemented-seam`, `dead-code`, `test-theatre`, `suppressed-error`,
`scope-creep`, `unbacked-claim`, and `safety-rule` for a lane-specific spec breach.

## Known false positives: do not flag (from the source rubric)

- A pre-existing issue this diff did not introduce.
- Something that looks like a bug but is correct.
- A pedantic nitpick a senior engineer would not raise.
- Something a linter would catch. Do not run the linter to verify.
- A general code-quality concern, such as missing coverage or generic security posture, unless a
  CLAUDE.md rule explicitly requires it.

## Known false positives: the Deguffer lanes specifically

The safety and machine-artefact lanes assert *absence*, "that does not exist", "that was not
updated", "that is already solved", and absence is the easiest claim to get wrong from an incomplete
search. Do **not** flag:

- A symbol you failed to find because you searched too narrowly. A partial class, a source
  generator, a XAML-generated field, and a string-keyed lookup all hide a definition from a naive
  grep. Search the whole repository before calling something phantom.
- Code unreferenced *within the diff* but reached from elsewhere: a provider consumed by iteration
  rather than by name, a test helper, a public API, a property bound only from XAML. "No caller in
  the hunk" is not "no caller".
- A deliberate divergence from a seam where the seam genuinely does not apply. The finding is a
  *duplicate* implementation, not any implementation you would have written differently.
- A `LongPath` call you assumed was missing without checking the caller one frame up. The seam is
  often applied at the boundary, not at every use.
- A pre-existing `TODO`, suppression or narrating comment that the diff merely moved, reindented or
  left nearby. The trigger is introduction, not existence.
- A test that is thin, or one you would have written differently. Only a test that **cannot fail**,
  or one whose assertions this diff **weakened**, is in scope.
- A missing §5.6 negative assertion on a change that does not alter what gets deleted. The rule
  attaches to deletion behaviour, not to every test in the file.
- An ordinary explanatory comment. Only a comment describing the *edit itself* is a finding. A
  comment explaining *why* the code is as it is, especially one citing a spec section, is exactly
  what G3 asks for.
- A scope judgement you are inferring rather than reading. If the intent from step 3 is vague, the
  author gets the benefit of the doubt.
- Anything you would phrase as "consider", "might be cleaner" or "could be simplified". That is the
  `/simplify` skill's job, not this one.
