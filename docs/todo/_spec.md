# Deguffer — specification

> **Status:** 🟢 ACTIVE — the founding specification and the source of truth for the safety model.
> Implementation is underway: `Deguffer.Core` carries the safety model, the planner/executor, and
> the three Tier 1 providers (NuGet, npm, Gradle); `Deguffer.App` carries the WinUI 3 shell and the
> preview-first flow. This document describes the intended design in full, not the current state of
> the code — where they differ, the spec is the target.

**Deguffer** is a small Windows utility — **C# 14 / .NET 10 / WinUI 3** — that finds and reclaims
wasted disk space, with a safety model good enough to trust unattended. It recognises what specific
locations on a disk actually are, reports what each one costs to lose, and leaves the decision with
the user. It also shows where the machine's memory goes, and its one action there is to ask a program
to close itself (§7.2).

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

Memory is the other thing a Windows machine runs short of, and a user who wants to know what is
using theirs should not have to leave for a different program to find out. The subject has the same
trap in another form. Low free memory is Windows working as designed, because it fills memory
nothing else needs with cache. The tools that make a free-memory figure rise do it by trimming
working sets and purging the standby list, and the pages come back as page faults, read from disk
again once they have been purged or reused. The failures a user does feel are commit charge reaching
the commit limit, when allocations start to fail, and sustained hard faulting with the disk load to
match. §7.2 draws where physical memory goes and leads with commit charge, the one of the two a
single figure states. In its first phase it acts on none of it.
[memory-view.md](memory-view.md) records the investigation it rests on.

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
- **Show where memory goes**, as a picture of physical memory led by commit charge against the
  commit limit, without implying that any process, service or part of Windows is safe to close.
  See §7.2.

**Non-goals**

- Not a duplicate finder or an uninstaller.
- Not a general file manager. Explore (§7.1) opens, reveals and removes an individual file or
  folder the user has picked out of the picture, because that is the action a size view leads to.
  It does not browse, move, rename or copy, and it does not aim to replace Explorer.
- No automatic/scheduled deletion in v1. Nothing is removed without explicit confirmation.
- Not a Windows component cleaner — `WinSxS` and `Windows\Installer` are deliberately out of scope
  (see §9).
- Not a RAM cleaner, in any form. The memory view never trims a working set, never purges or
  flushes the standby list or any other memory list, and never offers any other way to push pages
  out of memory while the programs using them go on running. Windows reclaims those pages when it
  needs them, and pages pushed out early come back as page faults, read from disk again once they
  have been purged or reused.
- No service control from the memory view. It never starts, stops, pauses or reconfigures a service.
  It is not where Deguffer decides whether to control services at all: that question is open in
  [unreached-locations.md](unreached-locations.md), for the one disk case that needs it.

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
| `C:\ProgramData\Microsoft\VisualStudio\Packages` | 7.7 GB | The installer's payload cache, and a different directory from the row above — a machine has both. Removing it breaks an offline repair or modify, and there is no allow-list to be had: the children are one per payload, and the installer's own record of each installed product sits among them. See §9 |
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
destination, and the separation is the design.

As with the rest of this document, what follows is the target rather than a description of the code.
Explore draws, navigates, and acts on one selection at a time: it opens an item, shows it in
Explorer, puts the Windows properties sheet on it, and removes it to the Recycle Bin or, as a
deliberate second choice, outright. What it refuses is decided in `ExploreActionPolicy` and decided
again in `ExploreRemover` immediately before anything is deleted, so the rules below hold whether or
not the shell asked.

- **Explore never classifies.** It reports a name and a number. It never says a thing is safe, never
  pre-selects anything, and never orders anything by how removable it is.
- **Explore never pre-selects and never acts on more than the user picked out by hand.** Storage
  offers a plan; Explore acts on one selection at a time, and there is no "clean everything here".
  A selection may hold more than one item, because the user picked each of them out by hand; what it
  may never hold is anything they did not.
- **Explore refuses whatever the tier model would call Tier 4**, and it does not get to decide what
  that is. §5.2 is unconditional and is not scoped to a page: an unrecognised child of a tool's root
  *is* Tier 4, so `gradle.properties` beside `.gradle\caches` is refused here exactly as it is
  refused there. So are the §9 exclusions, `C:\Windows`, `Program Files`, and every path a provider
  names as protected. That includes what a provider can only name once it has asked the machine: a
  location a tool reports in place of its documented default, and a path a plan holds back because a
  program is using it. Explore reads all of that afresh when it is first opened and again whenever a
  scan starts, including whether each tool can be found on the `PATH` — which is re-read from the
  machine and user environment keys at that moment, because Windows never pushes an installer's
  change into a running process — so between scans it answers from the machine as it was at the last
  of those.
  `%LOCALAPPDATA%`, `%APPDATA%`, `LocalLow` and `%TEMP%` are refused as folders
  and are ordinary inside, as the profile is. **A folder holding any refused path is refused too**,
  because removing a folder removes what is in it — but only while that path is on disk, so the
  folder a tool leaves behind once its protected contents are gone stays removable.
- **What Explore shows and what Explore will act on are different sets**, and the second is much
  smaller. Drawing a rectangle for a path says only that the bytes are there. It is not a
  classification, it is not an offer, and the UI must never let a user read it as one — which is why
  Explore has no bulk action and no pre-selection, and why the refusals above are stated with their
  reason rather than by greying something out.
- **Removal from Explore goes to the Recycle Bin by default.** §8's fourth question concludes that
  undo is impossible at cache sizes, and that is true of a ten-gigabyte tree. It is not true of the
  one file a user picked out of a picture, and where recovery is available it is not optional.
  Removing permanently stays available as a deliberate second choice, and says what it is.
- **§5.6 still applies.** Every removal asserts afterwards that what should have survived did.
- **Explore's numbers may be lower bounds, and must say so.** The measurement rules differ from
  Storage's on purpose: a total that is short is unacceptable where it decides a deletion, and
  acceptable where it draws a picture — provided the picture states which it is.

### 7.2 Memory — where the memory goes

Storage answers **"what is safe to remove"**, and Explore answers **"where did the space go"**. Memory
answers **"where is the memory going"**, for physical memory rather than a drive, and it is a
destination of its own for the reason Explore is. [memory-view.md](memory-view.md) is the
investigation behind every rule here, with what was measured and what was not.

As with the rest of this document, what follows is the target. The first phase is a read-only view:
it closes, stops and terminates nothing. [memory-view-plan.md](memory-view-plan.md) states the order
it is built in.

- **Memory carries §7.1's discipline.** It never classifies: it reports a name and a number, and
  never says that a process, a service or a part of Windows is unneeded, idle or safe to close. It
  never pre-selects anything, and never orders anything by how closable it is. What it shows and what
  it would act on are different sets, and in the first phase the second one is empty.
- **The headline is commit charge against the commit limit, with available memory beside it.** Low
  free memory is normal: Windows fills memory nothing else needs with cache, and standby pages are
  available the moment something asks for them. Of the two failures a user feels, commit charge
  reaching the commit limit is the one a single figure states, and the one closing a program
  measurably relieves. The other, sustained hard faulting, shows in disk activity rather than in any
  figure here. A headline that led with memory "in use" would present a cache as a problem.
- **The picture is sized by private working set, and its numbers are lower bounds that say so.** The
  private working set is the closest single figure to what closing a program would return, and it is
  not exact. Compressed pages are held by the compression store rather than by their owner, shared
  pages outlive the process, a working set shrinks under pressure, and memory the kernel, drivers or
  the compositor hold on a process's behalf is not in it.
- **The tree has three parts.** *Applications* follows the process tree and holds every process that
  hosts no service, including one a service host started: Windows starts packaged applications and
  brokers that way, and they are not memory a service holds. *Services* is grouped by host process and
  names the services each host holds: memory in a shared host cannot be divided between its services,
  so the host is the smallest part the picture sizes, and nothing is drawn under a host. *Windows*
  holds the compression store, the non-paged pool, the free, modified and standby lists where those
  could be checked or the system cache where they could not, and the memory no figure attributes.
- **A parent link counts only where the creation times allow it.** Windows reuses process
  identifiers, so a "parent" created after its child is not its parent. A process whose parent has
  exited, or whose parent's identifier now belongs to a later process, sits at the top of its part. A
  process with children holds memory of its own as well as theirs, so its own share is drawn as a
  child of its own, as a folder's loose files are.
- **Services this account may not query may be missing, and the view says so.** Windows leaves them
  out of the service list without an error, so their hosts are drawn as ordinary processes.
- **Memory no figure attributes is drawn and labelled, never hidden.** A picture of only what
  processes hold accounts for part of physical memory, and a reader takes the rest for a leak, though
  most of it is cache that is already available. So the rest is one labelled part of Windows.
- **No page is drawn twice.** Where one figure already contains part of another, the second is not
  drawn as a part of its own, and hover text says where its pages are counted instead. The kernel's
  paged pool is one of those: the part of it in memory is inside the system working set, and the figure
  counts what is paged out as well, so it is not a part of its own. Where figures
  read a moment apart add up to more than physical memory, the view says so, rather than drawing a
  remainder below zero.
- **An undocumented figure is used only once it has been checked.** A process's private working set
  and its creation time sit in a part of the process table that Windows does not document. Before
  either is used, it is checked against Deguffer's own process, whose answer a documented call gives:
  the creation time exactly, and the private working set against the documented counter, with a
  documented fallback on a Windows too old to have that counter. The two sit in the same undocumented
  part of each record, so a check that fails for either turns both off. The view then draws no
  process at all, because it has no size to draw one by, no parent link it can trust and no identity
  to keep across a refresh. It says so, and still draws the figures Windows documents. It never shows
  zero in place of a figure, because zero reads as a process holding nothing.
- **Unelevated, the picture is incomplete, and it says which way.** The investigation measured
  nothing elevated. The exact breakdown of physical pages probably needs administrator rights, which
  is an inference that was not tested, so the unelevated picture draws what it can separate and puts
  the rest in the remainder.
- **A snapshot is stale as soon as it is read.** The view refreshes on a bounded cadence, never
  starts a read while one is still running, and keeps what the user is looking at across a refresh by
  process identifier and creation time, never by name.
- **Hover text may say what one of Windows' own parts is.** It never says that anything is safe to
  close, and it never suggests closing anything.
- **Memory is not a RAM cleaner, in any phase, and never controls a service** (§2).
- **Memory has one action, and §7.2.1 below specifies it.** The investigation found exactly one
  that fits this model: the program's own close, and nothing stronger.

#### 7.2.1 Closing a program

Memory's one action, and the one §7.2 says is specified here before any of it is built. Sections 6
and 7 of [memory-view.md](memory-view.md) are the investigation behind every rule below, and its
refusal table is restated here rather than pointed at, because a refusal that lives only in a
finished investigation is a refusal a refactor drops.

Deguffer exists because a cleaner that removes the wrong thing loses data that cannot be recovered.
Closing a program can lose exactly as much — an unsaved document, a build half-way through, a
download — and unlike a deletion it has no Recycle Bin behind it. So Memory asks a program to close
itself, the way the close button on its own window does, and it does nothing the program cannot
refuse. **There is one verb here, and it does not grow a second.**

**What is sent**

- **The action is `WM_CLOSE`, and nothing else.** Windows documents it as the signal "that a window
  or an application should terminate", whose default handling destroys the window, and which an
  application "can prompt the user for confirmation" about first
  ([WM_CLOSE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-close)). Unsaved work is
  therefore the program's own question, asked in its own words, in its own dialog. This is §5.1 for
  a subject that is not a file: the program's own close beats anything Deguffer could do to it from
  outside.
- **It is posted, never sent.** `SendMessage` "does not return until the window procedure has
  processed the message"
  ([SendMessage](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessage)),
  so a program already busy behind a modal dialog would hold Deguffer there with it. Posting
  "returns without waiting"
  ([PostMessage](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew)),
  which is also what makes the watch below honest: the program has been asked, and what it does next
  is its own.
- **It goes to every window that qualifies, in the order Windows enumerates them.** Closing one
  window of a program that has several does not close the program, and a program with three
  documents open asks about each in its own words. The count is named in the confirmation, so three
  dialogs are not a surprise.
- **Nothing is sent twice, and nothing is chased.** One pass, no retry, no escalation, and no
  attention paid to a window the program opens afterwards. A program still running when the watch
  ends is reported as still running, and the user may pick it again.

**Which windows qualify**

A window qualifies only where all of these hold, and a process with no window that qualifies is
refused rather than attempted.

- **It is top-level and unowned.** `EnumWindows` enumerates top-level windows, and from Windows 8
  only those of desktop applications
  ([EnumWindows](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumwindows)).
  Dialog boxes and message boxes "are owned windows by default"
  ([Owned Windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)), and
  closing a dialog is not closing the program.
- **It is visible, and it is not cloaked.** A cloaked window has "all the trappings of visibility,
  without actually being presented to the user"
  ([The Old New Thing](https://devblogs.microsoft.com/oldnewthing/20200302-00/?p=103507)), which is
  how the shell holds a window on another virtual desktop. Windows documents the three cloaking
  values but never which cause produces which
  ([DWMWINDOWATTRIBUTE](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)),
  so Deguffer reads any cloak as "not on screen" and attaches no meaning to the value. A user whose
  program sits on another desktop is told that, rather than left with a close that appears to do
  nothing.
- **It is not a console host's window.** Closing a console sends `CTRL_CLOSE_EVENT` to every process
  attached to it, and the system ends a process that handles the event when its handler returns or
  after five seconds
  ([HandlerRoutine](https://learn.microsoft.com/en-us/windows/console/handlerroutine)). The console
  host makes one *attached* process the reported owner of its window, so a search for a program's
  own windows finds the console window too, and posting there would end every attached program
  without any of them asking about unsaved work. A graphical program can own a console window too:
  on a workstation checked on 2026-09-13, one program that is not a console program owned an
  invisible `ConsoleWindowClass` window. The visibility rule above would have excluded that window,
  and that is why the refusal below is of the whole process rather than of one window: whether a
  console's window is visible says nothing about which programs share the console.

**A console window is recognised by its class, and that is not a documented contract.** Microsoft
uses the name `ConsoleWindowClass` in a sample without ever documenting it as the console's class
([client-side UI Automation provider](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/create-a-client-side-ui-automation-provider)),
and the terminal's own source is where both it and `PseudoConsoleWindow` are defined
([window.cpp](https://github.com/microsoft/terminal/blob/main/src/interactivity/win32/window.cpp),
[InteractivityFactory.cpp](https://github.com/microsoft/terminal/blob/main/src/interactivity/base/InteractivityFactory.cpp)).
No documented call asks whether a window belongs to a console. So the refusal is of the whole process
rather than of the one window, and the residual risk is stated rather than designed away: were a
class name to change, Deguffer would stop recognising that console. The pseudoconsole's window is
caught twice over, because it is owned and never shown, and the alternative to the class check —
attaching to another program's console to ask — is worse than the risk.

**What the target is**

- **A held handle, not a number.** When the user confirms, Deguffer opens the process with
  `PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE`, the pair `ProcessProbe` already established as
  the one an unelevated Deguffer is granted for most processes, and reads the creation time back
  through it. If that time differs from the one in the snapshot the user picked from, the process
  they picked has gone and the number now belongs to another: the action is refused and says so.
- **The handle is held until the action finishes**, and that removes the reuse race rather than
  narrowing it. A process identifier "is valid from the time the process is created until all
  handles to the process are closed and the process object is freed; at this point, the identifier
  may be reused"
  ([PROCESS_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-process_information)),
  so while Deguffer holds one, nothing else can take the number it is about to post to.
- **Every window is checked against that handle immediately before its own message.** A window
  handle is recycled too, so the process behind each one is asked again at the moment of posting,
  and a window that has moved to another process receives nothing.
- **Exit is asked by waiting, never by an exit code.** A process may exit with `STILL_ACTIVE`'s own
  value, which both `ProcessProbe` and Windows record
  ([GetExitCodeProcess](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getexitcodeprocess)).

**What is refused, and why**

Every row here is a rule rather than the consequence of an access check failing, because unelevated
the investigation could open none of the twelve system processes it named. Where more than one row
applies, the first in this order is the reason shown, so the same process always gives the same
answer.

| Refused | Why |
| --- | --- |
| Terminating anything, however it is asked for | `TerminateProcess` ends every thread "immediately with no chance to run additional code" ([Terminating a Process](https://learn.microsoft.com/en-us/windows/win32/procthread/terminating-a-process)), so nothing the program holds is written. Memory has no stronger verb, and no preference adds one. |
| A process in another session, or running as another user | A window in another session cannot be reached from this one, and another user's program is not this user's to close. `ProcessIdToSessionId` answers the first and documents the access right it needs ([ProcessIdToSessionId](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-processidtosessionid)), the token's user answers the second, and a process whose session or user will not be read is refused with the rest. |
| Deguffer itself, and anything in its own process tree | Deguffer closing itself leaves the action unwatched, the result unwritten and §5.6 unrun. |
| The process owning the shell window, and any process whose image is `explorer.exe` or `dwm.exe` | Closing the shell or the compositor takes the desktop with it. `GetShellWindow` names one of them exactly ([GetShellWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getshellwindow)), and the shell is only "usually explorer.exe" ([The Old New Thing](https://devblogs.microsoft.com/oldnewthing/20190425-00/?p=102443)), so the names catch the rest. The compositor is expected to be refused by its account as well, and is named outright because the desktop is not a thing to stake on an expectation. |
| A critical process, and a process whose criticality will not be read | Ending a critical process stops the machine with `CRITICAL_PROCESS_DIED` ([0xEF](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0xef--critical-process-died)). `IsProcessCritical` needs only `PROCESS_QUERY_LIMITED_INFORMATION` ([IsProcessCritical](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-isprocesscritical)), so a process that will not answer it is refusing for a reason. |
| A process at a higher integrity level than Deguffer's own, or whose level will not be read | "The thread of a process can post messages only to message queues of threads in processes of lesser or equal integrity level" ([PostMessage](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew)), and Microsoft publishes no list of what passes the filter, only that write-type messages do not ([UIPI](https://learn.microsoft.com/en-us/previous-versions/dotnet/articles/bb625963(v=msdn.10))). The current `PostMessage` page says a blocked post sets the last error to 5, while the archived design note says a blocked call "return[s] success but silently drop[s] the window message". Two Microsoft sources disagree about what the post reports, so Deguffer decides before posting and never by what the post reports. Deguffer therefore reads the level from the token's mandatory label ([TOKEN_MANDATORY_LABEL](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-token_mandatory_label)) and compares the ranges the well-known RIDs define ([Well-known SIDs](https://learn.microsoft.com/en-us/windows/win32/secauthz/well-known-sids)), never equality, so a level between two named ones classifies correctly. |
| A process hosting any service | §2. Memory never controls a service, and a host's window is every service in it. **This is the one row that cannot be made a rule**, and saying so is better than implying a coverage it has not got: Windows leaves services this account may not query out of the list without an error, so §7.2 already draws a host it did not name as an ordinary process, and this row does not reach that one. Where the service list came back short, the confirmation says so in the same words §7.2's note uses. What stands between the user and such a host is the rest of the table, which refuses another session, another account and a process with no window that qualifies. Memory takes no view on the open question about service control in [unreached-locations.md](unreached-locations.md), which is about disk. |
| A process owning a console window, and any console host | Above. It has no close of its own to send, and the window it appears to own is not its. |
| A packaged application that is not running | Windows keeps a suspended packaged application in memory only while nothing else needs the pages, and reclaims it when something does, so closing one buys nothing a user waited for. `GetPackageFullName` names a packaged process with `PROCESS_QUERY_LIMITED_INFORMATION` ([GetPackageFullName](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getpackagefullname)); `IPackageDebugSettings::GetPackageExecutionState`, a method documented for debuggers, reports a package's state as one of `PACKAGE_EXECUTION_STATE`'s values, `PES_SUSPENDED` among them, and documents no access it needs ([GetPackageExecutionState](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ipackagedebugsettings-getpackageexecutionstate), [PACKAGE_EXECUTION_STATE](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-package_execution_state)) and `PSS_PROCESS_FLAGS_FROZEN` reports the same for one process ([PSS_PROCESS_FLAGS](https://learn.microsoft.com/en-us/windows/win32/api/processsnapshot/ne-processsnapshot-pss_process_flags)). Which of the two an unelevated Deguffer may ask is measured before either is relied on, and a packaged process whose state will not be read is refused. |
| A process with no window that qualifies | §5.2's reasoning, for a subject that is not a path: what has no recognised route is not offered, and a route Deguffer had to guess at is not a recognised one. A packaged application whose window belongs to the frame host rather than to itself falls here, which is why there is no rule about frame hosts: `EnumWindows` finds it no window of its own, and Deguffer does not go looking by any other route. |

**Every refusal is decided twice.** `MemoryActionPolicy` decides when the user selects a row, so the
reason is on screen before they try anything, and `ProcessCloser` decides again through the handle it
holds, immediately before the first message. This is §7.1's `ExploreActionPolicy` and
`ExploreRemover` for a subject that changes far faster than a disk does.

**Nothing is asked of Windows for a row nobody selected.** Session, account, integrity, criticality,
package state and window set are read for the one process the user picked, once, and again before the
action. A picture of five hundred processes redrawn every two seconds must not open five hundred
processes to decide what it would refuse, and a verdict nobody asked for is a verdict nobody reads.

**A refusal is a sentence on the row, never a disabled button.** §7.1's rule, unchanged, and the
reason every line of the table above carries a *why*.

**The confirmation is not a preference.** Tier 3's typed phrase is a preference because the preview
and Tier 3 never being pre-selected still stand behind it (§7). Nothing stands behind this one: a posted message
cannot be recalled, and what is at risk is another program's unsaved state. So a close is confirmed
every time, by a dialog that names the program, its identifier, how many windows will be asked, that
the program may ask the user about unsaved work, and that Deguffer will do nothing further whatever
the program decides. The words live in Core as `ExploreRemovalPrompt`'s do, because a sentence that
exists only inside a dialog is a sentence nothing can hold Deguffer to.

**The watch, and what the result says**

- **There is no deadline.** A save prompt waits for a person, so a timer would report "still running"
  about a program doing exactly what it was asked. Deguffer watches the handle it holds and reports
  the moment the process exits. The watch ends when the process exits, when the user dismisses the
  result, or when the user leaves the page: three ends, every one of them an event rather than a
  duration.
- **One close at a time.** While a watch is open, no second close is offered. That keeps one report
  about one action, and it is §7.1's "one selection at a time" for a subject that takes time to
  answer.
- **The result reports commit charge before and after, with available memory beside it**, as Storage
  reports free space (§7) and as §7.2's headline reads the machine. The before figure is taken as the
  first message is posted, and the after figure when the process exits, so a result that is still
  watching shows no after figure at all rather than a difference that means nothing yet. The result
  says in words that the machine went on allocating and freeing throughout, so the difference is what
  happened rather than what this close returned.
- **A program that is still running is reported, not escalated.** It asked, it refused, or it has
  work Deguffer cannot see. The result says so and offers nothing stronger, because there is nothing
  stronger to offer.
- **The report outlives the next reading.** The page redraws every two seconds and clears its reading
  notes each time. §5.6 exists to leave the user evidence, and evidence that disappears inside one
  cadence is not evidence, so a close's report is a surface of its own that the user dismisses.

**§5.6, for a subject that exits on its own**

A disk does not delete itself while Deguffer looks away. Processes exit constantly, and a closed
program's own children are expected to go with it, so "nothing else stopped" is not a claim this
action can make. §5.6 is met here by two assertions that are exact and two lists that assert nothing,
and the result says which of the four each line belongs to rather than smoothing the difference over.

1. **What Deguffer sent.** The action records every process it opened and every window it posted to,
   each of which belonged to the target at the moment of posting, checked through the held handle.
   Nothing was posted anywhere else, and nothing but `WM_CLOSE` was posted at all. That much is
   exact, and it is the assertion a test makes bite.
2. **That what this close could not have ended survived.** Deguffer's own process and its tree, the
   process owning the shell window, and every process named `explorer.exe` or `dwm.exe` in the
   snapshot taken before the action are looked for again when the watch ends, by identifier and
   creation time. Asking an ordinary program to close cannot end any of those, so this negative is
   exact, and a run that lost one fails and names it. The set is deliberately the one the *before*
   snapshot already knows, because §7.2.1 does not open five hundred processes to build a survivor
   list either. A critical process needs no line of its own here: ending one stops the machine, so
   Deguffer would not be running to report it.
3. **What was expected to go.** The target's descendants, taken from the creation-time-checked parent
   links (§7.2) in the snapshot before the action. A child of a closed program exiting is the program
   closing properly, so they are named as expected rather than counted as failures.
4. **What Deguffer does not claim.** Every other process in the before snapshot and not in the after
   one is listed as an exit Deguffer did not cause, beside the sentence that processes exit on their
   own. **A service host is one of those, and is deliberately not in item 2.** A demand-started
   service stops when whatever started it ends, and a shared host exits when its last service stops,
   so closing a program that was a service's only client can end a host without Deguffer having sent
   it anything. Listing that is honest. Failing the run over it would put a false alarm on the one
   surface §5.6 exists to make trustworthy. Memory never reports that nothing else stopped, because
   it cannot know that.

A close's evidence is shown on the §5.6 surface Storage already has, never on a second surface for
the same thing. `VerificationCheck` is keyed by a path and has no outcome for an expected exit or for
an exit Deguffer did not cause, so building the closer includes deciding how that shape carries a
process and those two outcomes.

**What this section does not authorise**

- No termination, in any form, at any tier, under any preference.
- No service control (§2), which includes closing a host's window to reach the services inside it.
- No working-set trim, no standby-list purge, and no memory list touched at all (§2).
- No elevation asked for in order to close something an unelevated Deguffer may not. A refusal here
  is an answer, not an obstacle.
- No bulk close, no pre-selection, and no ordering of anything by how closable it is (§7.2).

---

## 8. Open questions

1. **Detecting an unused toolchain.** Android SDK and PlatformIO are 12 GB combined and pure waste
   *if* idle. Last-access times are unreliable on NTFS by default — is there a better signal?

   **Answered for PlatformIO, and not with a signal.** `pio system prune --dry-run` reports which
   installed packages no installed development platform still requires. It is free to ask, it is
   non-destructive, and it is the tool's own judgement rather than an inference from a timestamp —
   so the provider offers that figure and nothing else, and offers no row at all where it comes back
   zero. The question narrows to the Android SDK, and to any toolchain whose own tooling will not
   answer it.
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

**The installer package caches are two directories, and both are named here rather than implied.**
`C:\ProgramData\Package Cache` and `C:\ProgramData\Microsoft\VisualStudio\Packages` sit apart, hold
the same kind of thing, and cost about the same — 6.7 GB in the founding audit, and 7.7 GB measured
on a Windows 11 workstation since, which makes the second the largest single location no provider
reaches. Naming only the first left the larger one excluded by a plural, and a reader could not tell
whether it had been considered.

Deguffer therefore **reports them and never offers them**: `%PROGRAMDATA%` is refused from Explore,
no provider targets either path, and both are named survivors on the one provider that declares that
root, so §5.6 produces evidence about them rather than silence. What the app does say is what each
one is and what clearing it costs, which is the reader's actual question when a size picture puts
seven gigabytes in front of them.

A Tier 2 provider was the alternative, and two rules rule it out:

- **§5.1's vendor route is not an eviction command.** `vs_installer.exe --nocache`, and the
  `KeepDownloadedPayloads` policy behind it, remove the existing payloads *for one product* as a side
  effect of the next install, modify or repair of that product. Nothing clears the cache on request.
  A step that frees no space when it runs cannot be previewed, cannot be reported against free space
  before and after, and gives §5.6 nothing to verify.
- **§5.2 cannot be satisfied by a path rule.** The measured machine held 1,249 children, one per
  payload, named by component and version and different on every machine and after every update. No
  allow-list can be written, so every child is unrecognised and therefore Tier 4 — and a deny-list is
  the shape §5.2 exists to forbid. `_Instances` makes the point concrete: it holds each installed
  product's own catalogue and state, beside the payloads, which is §5.2's "config lives next to
  cache" exactly.

`InstallCleanup.exe` is not an answer either. Microsoft documents it as a last resort after a repair
or uninstall has already failed, and warns that it can remove features belonging to other products.

**Outlook's data files are never removed, by any route, wherever they are saved.** An offline mailbox (`.ost`)
is routinely the largest single file on a business machine, and advice to delete it is everywhere.
It is not a cache in §3's sense. Microsoft documents a Sync Issues folder inside it that is never
copied to the server, and the one sentence permitting its deletion is conditional on an Exchange
account Deguffer cannot see. Outlook's own route, *Mail to keep offline*, is a shrink that
deliberately leaves the Outbox alone. A personal data file (`.pst`) is not a copy of anything at all:
it is where a POP or IMAP account delivers and where an archive goes.

No provider targets either, and absence from every allow-list is not enough on its own, because a
`.pst` is wherever somebody saved it — a temporary folder, a build directory, a data disk — and a size
picture puts it in front of them. So the rule is a type rather than a place, and every route asks it:

- A removal Deguffer performs itself steps over a store and leaves the folders holding it, and the
  measurement that forecasts it leaves the store out. A file named like a store is left even where it
  carries the mark of a link, because a OneDrive placeholder and a deduplicated file carry it too.
- A step whose removal is not Deguffer's to steer is withheld while a store is inside its reach: a
  tool's own command (§5.1), File History's cleanup, and a Recycle Bin on either route, since a bin's
  deleted items and the records that restore them go together. Immediately before a command runs or a
  bin is emptied, the disk is looked at again.
- Every store a plan finds is protected by its path, so §5.6 fails a run that lost one, whether or not
  the store's own row ran.
- Explore refuses a store by extension, together with Outlook's own folder under `%LOCALAPPDATA%` and
  any folder named `Outlook Files`, and refuses to move a folder holding one to the Recycle Bin.
  Hovering a store says what it is and how Outlook itself makes it smaller.

`docs/cache-locations.md` records the whole argument, and what would change the refusal.
