# Deguffer — specification

> **Status:** 🟢 ACTIVE — the founding specification and the source of truth for the safety model.
> Implementation is underway: `Deguffer.Core` carries the safety model, the planner/executor, and
> the three Tier 1 providers (NuGet, npm, Gradle); `Deguffer.App` carries the WinUI 3 shell and the
> preview-first flow. This document describes the intended design in full, not the current state of
> the code — where they differ, the spec is the target.

**Deguffer** is a small Windows utility — **C# 14 / .NET 10 / WinUI 3** — that finds and reclaims
wasted disk space, with a safety model good enough to trust unattended. It recognises what specific
locations on a disk actually are, reports what each one costs to lose, and leaves the decision with
the user.

## The name

**Guff** is British for nonsense, waffle, rubbish — the stuff that accumulates and serves no
purpose. **De-** removes it. *Deguffer*: the thing that takes the guff out.

It is also the domain's noun. Throughout this document, **guff** means "disk contents that can be
reclaimed" — which is deliberately not the same as "files that can be deleted", and §3 is entirely
about the difference.

---

## 1. Why

Windows' built-in Disk Cleanup and Storage Sense understand Windows' own caches. They know nothing
about the things that actually fill a drive: application and package caches, downloaded toolchains
and runtimes, per-workspace editor state, container disk images, search and index databases. Those
are the bulk of the waste, and each needs its own knowledge to clear safely.

Size alone cannot distinguish them, so ranking directories by size and letting the user guess is
not a design this can adopt — §3 is the alternative.

That is a statement about how Deguffer decides what to *offer*, not a refusal to show the user
their own disk. §7.1 adds a view that ranks by size and says so, kept deliberately apart from the
one that classifies, because the failure this paragraph describes is a size ranking presented as a
recommendation.

The evidence below comes from auditing one real workstation (Windows 11, ~330 GB system drive) that
had reached **5.6 GB free**. Targeted cleanup of three package-manager caches recovered **22.9 GB**
in a few minutes, without touching a single piece of user data.

---

## 2. Goals and non-goals

**Goals**

- Attribute disk usage to a *named cause*, not just a path — "Gradle build cache", not `~\.gradle`.
- Classify every candidate by how safe it is to delete, and be explicit about what "safe" means.
- Prefer a tool's own eviction command over deleting its folder.
- Never delete without a preview, and never delete something irreplaceable without saying so.
- Be fast enough to scan a full drive without the user walking away.
- **Show where the space actually went**, as a picture of the whole drive, without implying that
  any of what it shows is safe to remove. See §7.1.

**Non-goals**

- Not a duplicate finder or an uninstaller.
- Not a general file manager. Explore (§7.1) opens, reveals and removes an individual file or
  folder the user has picked out of the picture, because that is the action a size view leads to.
  It does not browse, move, rename or copy, and it does not aim to replace Explorer.
- No automatic/scheduled deletion in v1. Nothing is removed without explicit confirmation.
- Not a Windows component cleaner — `WinSxS` and `Windows\Installer` are deliberately out of scope
  (see §9).

---

## 3. The core model: safety tiers

Everything the tool knows about is one of four kinds. **This distinction is the product** — the
sizes are easy to compute, the classification is the part that takes knowledge.

| Tier | Meaning | Deletion consequence | Default |
| --- | --- | --- | --- |
| **1 — Regenerable cache** | Content that whatever produced it re-creates on demand, byte-for-byte or equivalently. | A slower next use. Nothing is lost. | Offered, pre-selected |
| **2 — Regenerable, with cost** | Re-created, but only by re-downloading gigabytes or re-indexing for minutes. | Time and bandwidth. | Offered, not pre-selected |
| **3 — User data** | Logs, histories, saved sessions. Looks like cache, *is not*. | **Gone permanently.** | Shown, never pre-selected, explicit warning |
| **4 — Do not touch** | Config, credentials, live application state, anything the tool cannot prove is idle. | Breakage. | Excluded from the UI entirely |

### The mistake this model exists to prevent

During the audit, ~11 GB of VS Code `workspaceStorage` was initially classified as cache. It is
mostly **AI chat session history** (`chatSessions`, `chatEditingSessions`) — a permanent record of
past conversations, sitting in a directory whose name and location strongly suggest "cache".

Nothing about its path, size or shape distinguishes it from genuinely disposable state. Only
knowing what the subfolder *contains* does. **A size-ranked directory list would have recommended
deleting it, and the user would have silently lost months of history.** Tier 3 exists because that
class of error is invisible until it is irreversible.

---

## 4. Evidence: what actually consumed the space

Observed on one workstation; sizes will vary, but the *shape* generalises. Paths use
`%USERPROFILE%` / `%LOCALAPPDATA%` conventions.

### 4.1 Verified reclaimed — Tier 1 (this cleanup: 22.9 GB)

| Source | Observed | Correct reclaim method | Notes |
| --- | --- | --- | --- |
| NuGet global packages + HTTP cache | ~10 GB | `dotnet nuget locals all --clear` | Also clears `v3-cache`, plugins cache and `NuGetScratch` — more than deleting `.nuget\packages` alone |
| Gradle caches + wrapper distributions | ~7 GB | Delete `.gradle\caches`, `.gradle\wrapper` | **Not** the whole `.gradle` folder — see §5.2 |
| npm cache | ~2.4 GB | `npm cache clean --force` | |

### 4.2 Strong candidates — Tier 1/2, not yet actioned

| Source | Observed | Tier | Notes |
| --- | --- | --- | --- |
| `%LOCALAPPDATA%\Microsoft\vscode-cpptools` | 6.0 GB | 1 | C++ IntelliSense databases; rebuilt on next open |
| `%LOCALAPPDATA%\Android` | 6.7 GB | 2 | SDK platforms + emulator images. Huge re-download; only offer if the toolchain looks unused |
| `%USERPROFILE%\.platformio` | 5.7 GB | 2 | Embedded toolchains; same reasoning |
| `%USERPROFILE%\.cache` | 3.8 GB | 1 | Mixed tool caches; needs per-subfolder rules |
| `%LOCALAPPDATA%\uv` | 3.5 GB | 1 | Python (uv) package cache |
| `%LOCALAPPDATA%\Temp`, `C:\Windows\Temp` | 5.4 GB | 1 | See §5.3 — needs exclusions and an age filter |
| Docker build cache | ~0.9 GB | 1 | `docker builder prune`; see §5.4 for the VHDX trap |

### 4.3 Tier 3 — user data wearing a cache costume

| Source | Observed | What it really is |
| --- | --- | --- |
| `%APPDATA%\Code\User\workspaceStorage` | 11.3 GB | Per-workspace editor state, **dominated by AI chat history**. Largest single workspace ~4 GB, of which ~3.3 GB was `chatSessions` |
| `%APPDATA%\Code\User\History` | 1.1 GB | Local file history (an undo record) |
| Agent/assistant session directories | ~1.7 GB | Conversation history and persistent memory. Deleting loses context permanently |

Per-workspace folders can be pruned individually, which makes a good UI: list workspaces by size
and last-modified, let the user drop dormant ones and keep recent ones. The last-modified date is
the single most useful signal here.

### 4.4 Tier 4 / out of scope

| Source | Observed | Why excluded |
| --- | --- | --- |
| `C:\Windows\WinSxS` | 16.1 GB | Never safe to delete manually. Only `DISM /StartComponentCleanup` may touch it, and `/ResetBase` blocks update rollback |
| `C:\Windows\Installer` | 12.5 GB | Orphaned MSI patches. Deleting the wrong file breaks repair *and* uninstall, permanently |
| `C:\ProgramData\Package Cache` | 6.7 GB | Visual Studio installer cache; removing breaks repair/modify |
| `pagefile.sys` | 5.0 GB | System-managed |
| Application VM disk images (e.g. `*.vhdx` under a packaged app) | 8.1 GB | Live application state |

---

## 5. Hard-won rules

These are the findings that cost the most to learn. Each should be a design constraint, not a
guideline.

### 5.1 Prefer the tool's own eviction command

`dotnet nuget locals all --clear` cleared four separate locations, two of which were not under
`.nuget` at all. A path-based cleaner would have missed ~3 GB. Where a package manager offers an
official cache command, call it and parse the result; fall back to path deletion only when none
exists.

### 5.2 Config lives next to cache — target subfolders, never roots

- `%USERPROFILE%\.gradle` contains `caches` and `wrapper` (disposable) alongside
  `gradle.properties` (user config, **may contain signing keys and credentials**).
- `%USERPROFILE%\.nuget` may contain `NuGet.Config` beside the `packages` cache. On the audited
  machine the config lived in `%APPDATA%\NuGet` instead — **so its location cannot be assumed**;
  probe both.

Rule: **never delete a tool's root directory.** Enumerate known-disposable children explicitly, and
treat anything unrecognised as Tier 4.

### 5.3 Temp is not free real estate

Blanket-clearing `%LOCALAPPDATA%\Temp` is the classic mistake. Observed: an active agent session
held 344 MB of live working files there, and 34 editor processes plus 4 Node processes held open
handles. Requirements:

- Exclude paths belonging to running processes.
- Apply an age filter (default: older than 7 days) rather than deleting everything.
- Treat "access denied" as normal and skip silently — a locked file is the OS protecting live state.

### 5.4 Freeing space inside a virtual disk does not free it on the host

Docker's `docker_data.vhdx` grows but never shrinks on its own. `docker system prune` frees space
*inside* the image while the host file stays the same size. Reclaiming it needs a separate compact
step (`Optimize-VHD`, or the vendor's own disk-cleanup). **Report the two numbers separately** or
the user will prune, see no change, and lose trust in the tool.

### 5.5 Recursive enumeration is too slow to be the scanner

Measuring a handful of profile subtrees with naive recursive directory enumeration **exceeded a
10-minute timeout** during this audit. A full-drive scan on that basis is unusable. The scanner
must:

- Read the **NTFS Master File Table** directly (or the USN journal) rather than walking directories.
- Fall back to parallel enumeration with a bounded worker pool only where MFT access is unavailable.
- Cache results with invalidation, so re-opening the tool is instant.
- Stream partial results to the UI — never block on a complete scan.

### 5.6 Verify the negative after acting

After deleting, assert that the things that should have survived *did*: config files, protected
directories, live session state. The audit confirmed six such paths explicitly. This is cheap and
turns "I think it worked" into evidence — and it catches an over-broad rule on the first run rather
than the hundredth.

---

## 6. Platform and architecture

### 6.1 Toolchain (decided)

| Choice | Value |
| --- | --- |
| Language | **C# 14** |
| Runtime | **.NET 10** (LTS) |
| UI framework | **WinUI 3**, via the Windows App SDK |
| Target framework | `net10.0-windows10.0.19041.0` (both projects) |
| Deployment | **Unpackaged** — see the note below |
| Minimum OS | Windows 10 1809 / Windows 11 |

`LangVersion` needs no explicit setting: the .NET 10 SDK defaults to C# 14. Pin it only if a
future SDK bump must not silently change language semantics.

`Deguffer.Core` stays free of any UI dependency, but still targets `net10.0-windows` rather than
plain `net10.0` — the scanner P/Invokes Win32 for MFT access, so there is no meaningful portable
subset to preserve. It is testable as an ordinary class library.

### 6.2 Project layout

```
Deguffer.sln
Deguffer.Core/         ← no UI dependency; unit-testable
  Scanning/            MFT reader, size aggregation, cancellation
  Providers/           one class per known cache (NuGet, Gradle, npm, Docker, VS Code, …)
  Safety/              tier classification, process/lock detection, exclusion rules
  Execution/           dry-run planner, executor, post-verification
Deguffer.Core.Tests/   ← provider rules and tier classification
Deguffer.App/          ← WinUI 3 shell, MVVM over Core
```

**Provider model.** Each source implements a common contract:

```csharp
interface ICleanupProvider {
    string Name { get; }               // "Gradle build cache"
    SafetyTier Tier { get; }
    Task<bool> IsPresentAsync();
    Task<long> EstimateBytesAsync(CancellationToken ct);
    Task<CleanupPlan> PlanAsync(CancellationToken ct);   // exact paths / command, never executed here
    Task<CleanupResult> ExecuteAsync(CleanupPlan plan, IProgress<double> p, CancellationToken ct);
    Task<VerificationResult> VerifyAsync(CleanupPlan plan);  // §5.6 — assert survivors
}
```

Adding support for a new cache is then one class plus tests, and the safety model applies uniformly.

**On naming inside the code.** The whimsy stays in the product name and the user-facing copy; the
API keeps plain, boring identifiers (`ICleanupProvider`, `CleanupPlan`, `SafetyTier`). A contributor
reading `ICleanupProvider` knows what it is immediately, whereas `IGuffProvider` has to be learned
first — and a joke that must be explained in every code review stops being one. Namespaces carry the
brand (`Deguffer.Core.Providers`); types describe what they do.

### 6.3 Platform notes

- WinUI 3 is fine for this, but **unpackaged** deployment is simpler here: a packaged app runs
  virtualised against `%LOCALAPPDATA%` in ways that complicate reading other apps' caches.
- Ship **self-contained** rather than framework-dependent. A disk-cleanup tool is exactly the thing
  someone reaches for on a machine that is too full to install a runtime, so requiring a separate
  .NET 10 install would defeat it at the moment of need. **NativeAOT** *is* supported by WinUI 3
  (Windows App SDK 1.6 and later) and would roughly halve the footprint, but it implies trimming
  and a trimmed build dies at startup inside the XAML runtime — see
  [the evaluation](aot-and-single-file-evaluation.md) for what was measured and tried.
- MFT reading requires **administrator**; the app should run unelevated by default, scan what it
  can, and request elevation only for the fast scanner and for `C:\Windows\Temp`.
- Enable **long path** support (`\\?\` prefixes or the manifest opt-in). Node and NuGet trees
  routinely exceed `MAX_PATH`, and this is the most likely source of silent partial deletions.
- Deletion should be genuinely parallel — these trees are hundreds of thousands of small files, and
  wall-clock time is dominated by per-file overhead, not bytes.

### 6.4 Engineering gates (mandatory)

The safety model is only as good as the code that carries it, and the rules below exist because a
safety rule buried in a 900-line class stops being enforceable. These are **gates**: a change that
breaks one is not ready, whether or not it compiles and passes tests. `CLAUDE.md` restates them
operationally for contributors and agents.

| Gate | Rule |
| --- | --- |
| **G1** | No monolithic files or objects. One responsibility per type; SOLID where it earns its keep. Soft ceiling ~250 lines per file. |
| **G2** | No god objects. Nothing both decides policy and performs I/O; orchestration and cleanup knowledge never live in the same type. |
| **G3** | No AI-trope or junior-engineer code — no comments restating the code, no ceremonial abstraction, no blanket `catch (Exception)`, no speculative generality. |
| **G4** | High performance, caching, and object reuse. Bounded parallelism, streaming enumeration, cancellation on every async path. |
| **G5** | Never recreate objects unnecessarily. Stateless collaborators are injected singletons; derived values are cached, not recomputed. |
| **G6** | Work in git worktrees — multiple agents may be operating on this repository concurrently. |
| **G7** | Use sub-agents for fan-out work; keep synthesis and final judgement in the main thread. |

Two of these are load-bearing for the safety model rather than merely stylistic:

- **G1/G2 make §5.2 auditable.** Each provider owns its own `DisposableChildSet` and nothing else
  owns any part of it, so "which children may this tool delete?" is answerable by reading one
  declaration. In a god object, that answer is spread across a method body and cannot be tested
  in isolation.
- **G4's bounded parallelism is a safety property, not just a speed one.** Unbounded fan-out over
  a deletion tree makes failure ordering non-deterministic, and §5.6's verification depends on
  knowing what was attempted.

### 6.5 Visual style — Acrylic (decided)

Deguffer uses a **glass-like Acrylic** backdrop for its windows: `DesktopAcrylicBackdrop` set as the
window's `SystemBackdrop`, with `ExtendsContentIntoTitleBar` so the material runs the full height of
the window rather than stopping under a solid title bar. Layout roots stay transparent; surfaces sit
on Fluent layer brushes so the material shows through.

Deliberate points, and the traps that come with them:

- **Acrylic, not Mica — a considered departure.** Fluent guidance reserves Acrylic for transient,
  light-dismiss surfaces and suggests Mica for a long-lived window base. Acrylic is the intended look
  here. Two practical consequences follow: it costs more GPU/battery than Mica on a large always-open
  window, and it is the right call for this app's floor anyway, since `MicaBackdrop` requires
  Windows 11 while `DesktopAcrylicBackdrop` reaches back to Windows 10 1809 — matching §6.1.
- **The backdrop will sometimes not be there.** Windows drops it under battery saver, with
  *Transparency effects* switched off, over Remote Desktop, and on some virtual machines. The system
  falls back to a solid colour on its own, but **the UI must stay fully legible with no translucency
  at all** — never encode meaning in the material, and never place text where it needs the blur to
  be readable.
- **Contrast is the real risk.** Text and tier colours sit over unpredictable desktop content. Use
  theme brushes (`TextFillColorPrimaryBrush`, `AcrylicBackgroundFillColorDefaultBrush`, the
  `LayerFillColorDefault` family) rather than fixed colours, and verify against a light desktop, a
  dark desktop and a busy photo. **High-contrast mode must disable the backdrop entirely.**
- Respect both **light and dark** themes, and follow the system setting by default.

---

## 7. UX principles

- **Group by cause, sort by size, colour by tier.** The first screen answers "what is eating my
  disk", not "what folders exist".
- **Every row states what happens on next use, and reaches it without a pointer**: "re-downloads on
  next build (~10 GB)" is more useful than a checkbox. The list has two densities, and the shipped
  one is **Compact**, which trades the sentence off the row for the whole list being on screen at
  once — a set of rows to choose between is the thing the first screen is for, and six rows with a
  paragraph each is not that set. Compact keeps the sentence one activation away: on the row's
  tooltip for a pointer, and inside the row's own disclosure for a keyboard, a screen reader or a
  finger. **Standard** writes it out under every name. What is not negotiable in either is that no
  row may be offered for deletion without its sentence being reachable *from that row*, by whatever
  the reader is using.
- **A row that is absent says which kind of absent it is.** "The tool is not on this machine" and
  "Deguffer has not been told where to look" are opposite in what they ask of the user, and only the
  second is something they can act on. A list that hides the first kind by default must never hide
  the second, because build output is usually the largest thing here and that row is the only place
  the app says so.
- **Tier 3 says plainly what is unrecoverable**, and by default it is confirmed before deletion.
  *Saying* it is not negotiable. *How hard the confirmation is to give* is the user's to set, and
  the two are separate. Typing a provider's name for every emptied Recycle Bin is transcription
  rather than deliberation, and a gate the user resents is a gate they learn to get past without
  reading it, so the typed phrase is a preference and it is off by default. Switching it off retires
  Tier 3's *own* question rather than the question: the row falls to the blanket confirmation, which
  names it, quotes the same "user data, permanent, cannot be undone" sentence the typed dialog would
  have carried, and totals what is going. Switching that off as well is a second, separate choice,
  and what then stands between the user and the deletion is the preview below and Tier 3 never being
  pre-selected. **No preference reaches Tier 4**, which stays excluded however the settings are left.
- **Dry run is the default action.** The primary button previews; deleting is the second step.
- **Show free space before and after**, prominently. It is the only number the user came for.
- **Age is a first-class column** for per-workspace and per-project data — "last touched 5 months
  ago" drives the decision more than size does.
- **The Acrylic backdrop (§6.5) is decoration, never information.** Tier, risk and selection state
  must all read correctly on a flat solid background, because on plenty of machines that is exactly
  what the user will see.

### 7.1 Explore — the other question

Storage answers **"what is safe to remove"**. Explore answers **"where did the space go"**. §1 says
plainly that the second question cannot be allowed to answer the first: size alone cannot tell a
package cache from eleven gigabytes of chat history, and §3 exists because a size-ranked list would
have recommended deleting the second.

Both questions are real, and a tool that answers only the first sends the user to a different
program the moment its providers do not recognise what filled the disk. So Explore is a separate
destination, and the separation is the design:

- **Explore never classifies.** It reports a name and a number. It never says a thing is safe, never
  pre-selects anything, and never orders anything by how removable it is.
- **Explore never pre-selects and never acts on more than the user picked out by hand.** Storage
  offers a plan; Explore acts on a selection, one item at a time, and there is no "clean everything
  here".
- **Nothing in Explore may reach Tier 4.** The §9 exclusions, `C:\Windows`, `Program Files`, and any
  path a provider protects are refused outright, with the reason stated. A path Explore does not
  recognise is not thereby safe — it is merely unclassified, which is exactly what the user is being
  told.
- **Removal from Explore goes to the Recycle Bin by default.** §8's fourth question concludes that
  undo is impossible at cache sizes, and that is true of a ten-gigabyte tree. It is not true of the
  one file a user picked out of a picture, and where recovery is available it is not optional.
  Removing permanently stays available as a deliberate second choice, and says what it is.
- **§5.6 still applies.** Every removal asserts afterwards that what should have survived did.
- **Explore's numbers may be lower bounds, and must say so.** The measurement rules differ from
  Storage's on purpose: a total that is short is unacceptable where it decides a deletion, and
  acceptable where it draws a picture — provided the picture states which it is.

---

## 8. Open questions

1. **Detecting an unused toolchain.** Android SDK and PlatformIO are 12 GB combined and pure waste
   *if* idle. Last-access times are unreliable on NTFS by default — is there a better signal?
2. **Per-workspace attribution for editor state.** `workspaceStorage` folders are opaque hashes;
   the mapping to a real path lives in each folder's `workspace.json`. Worth confirming this is
   stable across editor versions before depending on it.
3. **Should Tier 2 re-download estimates be measured or declared?** Showing "≈10 GB to restore"
   changes decisions, but measuring it accurately is hard.
4. **Undo.** Probably genuinely impossible for these sizes — Recycle Bin is not viable at 10 GB.
   If so, say so in the UI rather than implying reversibility.

---

## 9. Deliberately excluded

Windows component cleanup (`WinSxS`, `Windows\Installer`, installer package caches). These are
large and tempting — ~35 GB on the audited machine — but the failure modes are severe (broken
uninstall, unbootable rollback) and the safe operations are already exposed by `DISM` and the
vendors' own tooling. A tool that is trusted to clear caches should not stake that trust on Windows
servicing internals.
