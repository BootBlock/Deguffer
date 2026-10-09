# Deguffer review rules

Deguffer's own rules for the global `auto-review` skill, which reads this file at its step 2. The
skill holds the levels, the lanes, the steps and checks A to H; this file holds only what is
Deguffer's. Deguffer deletes directories on other people's machines, so its failure mode is silent,
irreversible data loss.

## Rules to collect

- Always: the root `CLAUDE.md`, and the section of `docs/todo/_spec.md` that governs the change.
  Name that section in the step 3 brief: the spec outranks an issue's phrasing.
- Memory notes are in `P:/Source/!Memories/Deguffer/`. Collect the ones for the kinds of change in
  the diff:

| The diff changes | Collect |
| --- | --- |
| A path, an enumeration or a deletion (§6.3) | *Test Deguffer long-path handling by the form of the path* |
| Code that asks whether a location exists | *A Deguffer presence probe must never read a refusal as absence* |
| A `Deguffer.App` view-model, or `Deguffer.App.Tests` | *A Deguffer view-model's decisions go to Core* |
| A bound list | *A live list is updated in place, never rebuilt* (in `Global/`) |
| A control inside a `ContentDialog` | *A ContentDialog content subscription can miss changes* |
| A test | *Deguffer verify by mutation* |
| A document under `docs/todo/` | *Deguffer plan docs carry a status banner* |
| A phase of `docs/todo/duplicates.md` | *Running a phase of the Deguffer duplicates plan* |
| A provider | `docs/cache-locations.md`, which describes every provider that ships |

## Compliance checklist (lane 1)

Quote the `CLAUDE.md` rule each finding breaks.

- **G1, one responsibility.** A file well past about 250 lines with no stated reason. A type that
  needs "and" to describe it. Core reaching `Environment.GetFolderPath`, `Process.Start`,
  `Process.GetProcessesByName` or `Registry` directly rather than through `IUserEnvironment`,
  `IProcessRunner` or `IProcessInspector`. A new cache source added as an arm of a switch rather
  than as a new `ICleanupProvider`. A view-model decision that belongs in Core.
- **G2, no god objects.** A type that "manages", "handles" or "processes". A type that both decides
  policy and performs I/O. `CleanupPlanner` gaining cleanup knowledge, or a provider gaining
  orchestration.
- **G3, no AI-trope code.** Each item in the `CLAUDE.md` list: a comment restating the code, an
  interface with one implementation and no test seam, a factory that only calls `new`, a wrapper
  forwarding every member, a `catch (Exception)` that swallows or rethrows unchanged, a null check
  on a value that cannot be null, a speculative knob or `virtual`, stringly-typed state, `#region`,
  a `Manager`/`Helper`/`Utils` type, a "Part 2" file.
- **G4, performance.** `GetFiles`/`GetDirectories`/`GetFileSystemEntries` where `EnumerateX`
  belongs. A tree materialised to count it. `Task.Run` fan-out with no `MaxDegreeOfParallelism`. A
  subprocess or filesystem probe repeated where it should be cached for the operation (asking npm
  where its cache is more than once is the canonical case). An async path with no
  `CancellationToken`. An `IEnumerable<T>` enumerated twice. A bound list cleared and refilled.
- **G5, object reuse.** `new ProcessRunner()` or `new UserEnvironment()` per call rather than
  `ProcessRunner.Default` and `UserEnvironment.Current` injected once. A `Regex`, `SearchValues`,
  comparer or lookup set built per call rather than `static readonly`. A directory re-measured after
  planning measured it.
- **No secrets or personal data.** A real path, profile, machine or share name, or pasted scan or
  log output, in source, a test, a fixture, a comment or a commit message. A fixture path is
  recognisably invented, such as `C:\Users\testuser\...`.
- **Public-repository hygiene.** Agent process in a commit message or a comment: a worktree name,
  review mechanics, the agent's reasoning.
- `TreatWarningsAsErrors` is on, so a compiler warning the diff introduces is a build failure.

## Lane 8: the safety lane

**Required at every level whenever the diff touches `Deguffer.Core/**`**, and then it takes lane
2's place. Category slug: **`safety-rule`**. It reads the spec and the code around the hunk, and
builds a concrete scenario in which something the user did not choose gets deleted. Flag:

- **§5.1** A path deleted directly where the tool offers its own eviction command.
- **§5.2** A tool's root directory reaching a target list. A recognised-child list widened to a
  wildcard, a prefix match or "everything except". A classification path on which an
  unrecognised child can come out as anything but Tier 4: an unknown thing silently treated as
  safe is the dangerous direction.
- **§5.6** A change to what gets deleted whose test asserts only that the target went. The negative
  assertion, that the tool root, unrecognised siblings and anything in Tier 4 survived, is the half
  that catches over-reach.
- **§6.3** A filesystem path that does not go through `LongPath`. A raw `Path.Combine` result handed
  to a delete or an enumeration is a silent partial deletion past `MAX_PATH`.
- **§6.5** Legibility that depends on the Acrylic backdrop.
- **Tier drift.** A provider gaining a recognised child with no test proving an unrecognised
  sibling still lands in Tier 4.

*Evidence:* the spec section and the `file:line` that breaks it. A safety finding without a cited
section is not a finding. The validator opens `docs/todo/_spec.md` at the cited section and
confirms it says what the finding claims: a misquoted section is worse than no finding.

## Known instances of checks A to H

- **A. Phantom surface.** A member called on `ICleanupProvider` that the interface does not declare.
  A XAML `x:Name`, `{Binding}`/`{x:Bind}` path, `{StaticResource}` key or converter with no backing
  property, resource or registration (XAML binding resolves at run time). A Segoe Fluent Icons
  glyph that is not the character intended: a private-use glyph reads back as empty through the
  file tools, so verify it by character code. A spec section that `docs/todo/_spec.md` does not
  contain. A member the pinned NuGet version does not provide. A `PreferenceStore` key read that
  nothing writes.
- **B. Half-applied parallel edit.** A new `ICleanupProvider` not registered in
  `CleanupPlanner.CreateDefault`, or missing from `docs/cache-locations.md`. A new `SafetyTier`
  member with no arm in the badge, the tooltip, the converter or the ordering. A new preference with
  no default in `PreferenceStore` or no read at its point of use. A recognised child added with no
  unrecognised-sibling test. A rename missed in XAML, which the compiler does not check. A cache
  location added to discovery but not to the size measurement, or the reverse.
- **C. Re-implemented seam.** A path joined or normalised outside `LongPath`; `Process.Start`
  outside `IProcessRunner`; `Environment.GetFolderPath` or `SpecialFolder` outside
  `IUserEnvironment`; a process check outside `IProcessInspector`; a directory walk beside
  `DirectoryScanner`; a second size measurement; a deletion path beside `DirectoryRemover`; a
  provider building its own scanner or discovery where `CleanupPlanner.CreateDefault` shares one.
  Here the second implementation is usually the one that misses the long-path case.
- **D. Dead on arrival.** G3's speculative generality: an abstraction nobody calls.
- **E. Test theatre.** All recorded here, all green: a deletion test with no §5.6 negative
  assertion; a long-path test that builds a deep tree past `MAX_PATH`, which passes on a machine
  with `LongPathsEnabled` whatever `LongPath` does (assert the `\\?\` form instead); a tier test
  covering only recognised children; a fixture that makes the assertion vacuous, such as a "do not
  double-count directories" test whose directories hold zero-sized data streams; a `Skip` left on a
  `Fact`; a test that reaches the real machine rather than `FakeUserEnvironment` and the process
  seams.
- **F. Suppression.** `#pragma warning disable`, a `!` on a value that can be null, a widened
  nullable annotation, `catch (Exception)` or `catch { }`, a log-only `catch`, a `??` fallback for a
  value that should never be missing. Under `TreatWarningsAsErrors` a suppression is usually a way
  past the build.
- **G. Scope creep.** An unrelated NuGet package: a new dependency needs a stated reason and a
  licence check.
- **H. Unbacked claim.** "Verified" in a commit message for a change nobody drove, the case this
  project cares about most. A `Debug.WriteLine` left in.

## Where definitions hide (check A)

A partial type split across files (`LongPath.cs` and `LongPath.Canonical.cs`); CommunityToolkit.Mvvm
source generators (`[ObservableProperty]` partial properties, `[RelayCommand]` commands named
`<Method>Command`); XAML-generated fields for `x:Name`; `[GeneratedRegex]` and `[LibraryImport]`
partial methods; string-keyed lookups such as `PreferenceStore` keys and XAML resource keys; and the
projects outside the product, `Deguffer.Testing` (the fakes) and `Deguffer.Benchmark`. Never take
`bin/` or `obj/` as evidence: they hold stale generated output.

## Deguffer's own false positives

- A `LongPath` call assumed missing without reading the caller one frame up: the seam is often
  applied at the boundary.
- A missing §5.6 negative assertion on a change that does not alter what gets deleted.
- A `catch` of a specific exception with a comment saying why it is expected: G3 asks for it.
- A comment citing a spec section to explain *why*: G3 asks for it.
- A provider reached only by iteration over `CleanupPlanner.Providers`, or a property bound only
  from XAML: neither has a caller by name.
