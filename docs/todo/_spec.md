# Deguffer — specification

> **Status:** 🟢 ACTIVE — the founding specification and the source of truth for the safety model.
> Implementation is underway: `Deguffer.Core` carries the safety model, the planner/executor, and
> the three Tier 1 providers (NuGet, npm, Gradle); `Deguffer.App` carries the WinUI 3 shell and the
> preview-first flow. This document describes the intended design in full, not the current state of
> the code — where they differ, the spec is the target.

**Deguffer** is a small Windows utility — **C# 14 / .NET 10 / WinUI 3** — that finds and reclaims
wasted disk space, with a safety model good enough to trust unattended. It recognises what specific
locations on a disk actually are, reports what each one costs to lose, and leaves the decision with
the user. It also shows where the machine's memory goes, and acts on none of it (§7.2).

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
nothing else needs with cache, and the tools that make a free-memory figure rise do it by trimming
working sets and purging the standby list, whose pages then come back as hard faults. What a user
feels is commit charge reaching the commit limit, when allocations start to fail. So §7.2 draws where
physical memory goes, leads with that figure, and acts on none of it.
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
  flushes the standby list or any other memory list, and never offers anything whose effect is to
  make a memory figure smaller. Windows reclaims those pages when it needs them, and pushing them out
  early brings them back as hard faults (§7.2).
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
  names as protected.
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
  available the moment something asks for them. The failure a user feels is commit charge reaching
  the commit limit, when allocations fail. A headline that led with memory "in use" would present a
  cache as a problem.
- **The picture is sized by private working set, and its numbers are lower bounds that say so.** The
  private working set is the closest single figure to what closing a program would return, and it is
  not exact. Compressed pages are held by the compression store rather than by their owner, shared
  pages outlive the process, a working set shrinks under pressure, and memory the kernel, drivers or
  the compositor hold on a process's behalf is not in it.
- **The tree has three parts.** *Applications* follows the process tree. *Services* is grouped by
  host process and names the services each host holds: memory in a shared host cannot be divided
  between its services, so the host is the smallest part the picture sizes. *Windows* holds the
  compression store, the system cache, the kernel pools, and the memory no figure attributes.
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
  drawn as a part of its own, and hover text says where its pages are counted instead. Where figures
  read a moment apart add up to more than physical memory, the view says so, rather than drawing a
  remainder below zero.
- **An undocumented figure is used only once it has been checked.** A process's private working set
  and its creation time sit in a part of the process table that Windows does not document. Before
  either is used, it is checked against Deguffer's own process, whose answer a documented call gives.
  A check that fails, or that has nothing documented to compare with, turns the figure off, and the
  view says so. It never shows zero in its place, because zero reads as a process holding nothing.
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
- **An action is specified here before any of it is built.** The investigation found one that fits
  this model: the program's own close, sent only to the visible windows of a windowed application in
  the user's own session, with everything else refused and its reason stated. Until this section
  specifies it, Memory has no action at all.

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
