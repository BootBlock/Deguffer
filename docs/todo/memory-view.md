# A memory view beside Explore — investigation

> **Status:** 📘 REFERENCE — the investigation is finished and nothing is scheduled. It answers
> "could Deguffer show where memory goes, the way Explore shows where disk space went, and help the
> user close what they do not need?" with *the picture, yes; the action, only in a narrow form; and
> neither inside the product [_spec.md](_spec.md) describes today*. Adopting any of it starts with
> §1 and §2 of the specification, not with code. Re-measure the figures before trusting them against
> a very different machine or Windows build.

Explore (§7.1) draws a drive as a picture sized by bytes. This asks whether the same picture could
be drawn for physical memory, sized by what each process, service and part of Windows holds, so a
user can see what is using it and gracefully close the applications and services they do not need.

**The short answer:**

- **Measuring is cheap and needs no elevation.** One `NtQuerySystemInformation` call returns memory
  figures for every process on the machine, including the ones this account may not open, in about
  8 ms.
- **No per-process number is "what closing this gives back".** The private working set is the
  closest, and it misses compressed pages, shared pages and memory the kernel, drivers and the
  compositor hold on the process's behalf.
- **A picture of RAM cannot add up without parts no process owns**, and separating those exactly
  probably needs administrator rights.
- **Closing things rarely helps, and a size picture invites the reader to believe it does.** Low free
  memory is Windows working as designed. The condition in which closing an application gives real
  relief is commit charge near the commit limit, so that is the number such a view would have to
  lead with, as Storage leads with free space.
- **The safe action is the application's own close**, never termination, and only for a windowed
  application in the user's own session. Services, console programs and windowless processes are
  shown and not offered, and services stay out at least until the open question about service
  control is decided.
- **Explore's layouts, hit tests and rasterisers could draw it**; its tree, knowledge and acting code
  are built on paths and could not be reused.
- **It is outside the product as the specification stands.** It would be the first feature about
  something other than disk, and the first to act on running programs.

## 1. What the specification and the code say

The specification defines Deguffer as a utility that "finds and reclaims wasted disk space", and
every goal in §2 is about disk. Nothing in it mentions memory, RAM or closing applications. Running
processes appear only as a guard on deletion: §5.3 excludes paths belonging to them.

The code matches. Deguffer never closes a process. `RunningProcessNotice` warns that a tool is
running and leaves what it holds open in place; `IProcessInspector` answers which named processes
are running and whether one is alive; `RunningProcessTable` reads image paths and working
directories so a directory in use is not removed. Nothing reads a memory figure, and nothing sends a
window a message, stops a service or terminates a process outside the tests.

[unreached-locations.md](unreached-locations.md) already leaves open whether Deguffer should control
services at all, for the one case that needs it (the search index), and leans towards no.

So whether to build this is a question about what Deguffer is, and it is the maintainer's. What does
carry over unchanged, if the answer is yes, is §7.1's discipline, and the rest of this document
assumes it:

- It never classifies: it reports a name and a number, and never says a process is unneeded.
- It never pre-selects, and acts on one selection the user picked out by hand.
- What it shows and what it will act on are different sets, and a refusal states its reason.
- Its numbers may be lower bounds, and must say so.
- Every action verifies the negative afterwards (§5.6).

## 2. What a memory number means

| Figure | Where it comes from | What it counts |
| --- | --- | --- |
| Working set | `WorkingSetSize` | This process's pages in RAM now, private and shared together. Summed across processes it counts every shared page once per process. |
| Private working set | `WorkingSetPrivateSize` (undocumented field, section 3); `PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize`; the `Process\Working Set - Private` counter | Pages in RAM that no other process can use. |
| Commit charge (private bytes) | `PagefileUsage` / `PrivateUsage`; `PrivatePageCount` | Private memory the process has committed, whether it is in RAM, compressed or paged out. Task Manager's *Commit size*. |
| Virtual size | `VirtualSize` | Address space reserved. It consumes no physical memory and is no use here. |
| Pool quota | `QuotaPagedPoolUsage`, `QuotaNonPagedPoolUsage` | Kernel pool charged to the process. |

**The private working set is the best single answer to "how much RAM would closing this return",
and it is not exact:**

- **Compressed pages are not in it.** Windows compresses pages it would otherwise page out, and they
  sit in the working set of the *Memory Compression* process, not the owner's.
- **Shared pages outlive the process.** An image or mapped file's pages move to the standby list when
  the last user exits, and Windows already counts standby pages as available, so closing a program
  does not raise *Available* by them.
- **The working set shrinks under pressure**, so on a busy machine it understates the footprint.
- **Memory held on the process's behalf elsewhere** — pool, page tables, the compositor's surfaces,
  GPU allocations — is not in it.

Commit charge answers a different question — how close the machine is to failing allocations — and
section 6 argues it is the more useful one. A view sized by private working set and headed by commit charge
states both honestly. Task Manager's default *Memory* column is the private working set; Microsoft
documents the counter behind it but not the column, and a Windows Internals co-author describes it as
the private working set, with a suspended packaged process shown as zero
([Yosifovich](https://scorpiosoftware.net/2023/04/12/memory-information-in-task-manager/)).

## 3. Measuring it

Measured unelevated on one Windows 11 (build 26100) workstation with 68.4 GB of physical memory and
490 processes, by a throwaway probe outside the repository. GB here means 10⁹ bytes.

| Measurement | Result |
| --- | --- |
| `NtQuerySystemInformation(SystemProcessInformation)`, one call | succeeded; 1.04 MB for every process, including those that refuse a handle |
| The same call, mean of 50 | 7.85 ms |
| Sum of working sets / private working sets / commit charge | 45.1 GB / 25.4 GB / 47.6 GB |
| `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` | 344 opened, 145 refused, 5 of those in the user's own session |
| Refused among the system processes checked by name | all of `System`, `Registry`, `Secure System`, `Memory Compression`, `smss`, `csrss`, `wininit`, `winlogon`, `services`, `lsass`, `dwm` and `MsMpEng` |
| `IsProcessCritical` over the 344 opened | no failures, none critical |
| Packaged (`GetPackageFullName` found a package) | 39 |

**`SystemProcessInformation` is the route.** It reads kernel bookkeeping, touches no page of the
processes it describes, and needs no handle, so the refusals above cost nothing but the ability to
act. Its documented structure names `WorkingSetSize`, `PagefileUsage`, `PrivatePageCount` and the
pool quotas ([NtQuerySystemInformation](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntquerysysteminformation)),
and warns that it may change. `WorkingSetPrivateSize` (offset `0x08` on x64) and `CreateTime`
(`0x20`) sit in the part it leaves undocumented; both have been stable since Windows Vista
([Chappell](https://www.geoffchappell.com/studies/windows/km/ntoskrnl/api/ex/sysinfo/process.htm)),
and .NET's own `Process` class parses the same structure. Walk the buffer by `NextEntryOffset`, since
thread records follow each process, and bound every offset and string by the returned length.

`RunningProcessTable` already sets the precedent for reading an undocumented layout: it checks the
offsets against its own process, whose answer is known, and turns the mechanism off with a note if
they disagree. The same check works here. The probe read `WorkingSetPrivateSize` for itself as
8.38 MB against 8.50 MB from the documented `PROCESS_MEMORY_COUNTERS_EX2`, two calls apart. That
structure needs Windows 10 22H2 or Windows 11 22H2 with the September 2023 update
([PROCESS_MEMORY_COUNTERS_EX2](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex2)),
and Deguffer's `TargetPlatformMinVersion` is 10.0.17763, so a check built on it needs a fallback
where the call fails.

**What not to use:**

- `System.Diagnostics.Process` has no private working set at all: `PrivateMemorySize64` is
  `PrivatePageCount`, which is commit
  ([ProcessManager.Windows.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessManager.Windows.cs)).
- `QueryWorkingSetEx` gives a per-page share count and so a proportional share of shared pages, but
  it needs `PROCESS_QUERY_INFORMATION`
  ([QueryWorkingSetEx](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-queryworkingsetex)),
  which protected processes refuse
  ([Process security and access rights](https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights)),
  and a page-by-page walk of every process is not a snapshot.
- Performance counters work unelevated, but the survey measured a single sample of
  `\Process(*)\Working Set - Private` at over a second. The cause was not found.

A snapshot is stale as soon as it is taken, and process identifiers are reused. Anything that acts
on a process identifies it by identifier *and* `CreateTime`, and checks both again immediately
before acting. Windows 11 26100.4770 documents `SystemBasicProcessInformation`, whose records carry
a `SequenceNumber` for detecting identifier reuse "instead of process CreateTime", and which the
same page recommends over `SystemProcessInformation` wherever it will do. Its structure holds no
memory figures, and it is newer than Deguffer's minimum Windows version, so it can replace the
creation time only as the identity check, and only where it exists.

## 4. The memory no process owns

Measured on the same machine, from `GetPerformanceInfo`
([PERFORMANCE_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-performance_information)),
unelevated:

| Figure | Value |
| --- | --- |
| Physical memory | 68.4 GB |
| Available (standby, free and zeroed) | 26.3 GB |
| System cache (standby plus the system working set) | 25.0 GB |
| Kernel paged pool / non-paged pool | 2.29 GB / 2.28 GB |
| Commit charge / commit limit | 60.7 GB / 74.8 GB |

Private working sets and the system cache come to 50.4 GB of the 68.4. The remainder holds shared
and shareable pages, pool, driver-locked memory, page tables, kernel stacks and free pages, and these
figures cannot divide it further without counting something twice. A picture that draws only the
processes' private working sets accounts for 25.4 GB, and a reader will take the other 43 GB for a
leak, though most of it is cache that is already available.

- **Unelevated, the lists are readable.** `SystemMemoryListInformation` succeeded without elevation
  on the probe machine, giving the free, zeroed, modified and standby page counts, standby by
  priority. `GetPhysicallyInstalledSystemMemory` less the physical total gives the hardware-reserved
  part ([GetPhysicallyInstalledSystemMemory](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getphysicallyinstalledsystemmemory)).
- **The exact breakdown probably needs elevation.** RAMMap's split into process private, mapped
  file, shareable, page table, pool, driver locked, kernel stack and so on needs the physical page
  database, which no documented API returns, and Sysinternals does not document how RAMMap reads it
  ([RAMMap](https://learn.microsoft.com/en-us/sysinternals/downloads/rammap)). The nearest published
  evidence is Geoff Chappell's: the input structure he gives for the prefetcher class also serves
  `SystemSuperfetchInformation`, and a user-mode caller of the prefetcher class must hold
  `SeProfileSingleProcessPrivilege`
  ([Chappell](https://www.geoffchappell.com/studies/windows/km/ntoskrnl/api/ex/sysinfo/query.htm)),
  which the unelevated token on the probe machine did not hold. Neither class was exercised here, so
  that the breakdown needs elevation is an inference.
- **Virtual machines are one process on the host.** WSL 2 appears as a `vmmem` process, holding
  the guest's page cache until it is reclaimed
  ([WSL memory reclaim](https://devblogs.microsoft.com/commandline/memory-reclaim-in-the-windows-subsystem-for-linux-2/)).

§7.1's rule decides the shape: an unelevated picture draws what it can separate, draws the rest as
one labelled part, and says the picture is incomplete; Explore's existing "Elevate and rescan" offer
is the precedent for the exact one.

## 5. Building a hierarchy

A treemap needs a tree, and processes are not one by nature.

- **Services map to their host process exactly.** `EnumServicesStatusEx` with
  `SC_ENUM_PROCESS_INFO` returns each running service's process identifier
  ([ENUM_SERVICE_STATUS_PROCESS](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-enumservicesstatusexw)),
  silently omitting services the caller may not query. On the probe machine it listed 131 running
  services in 122 host processes; every one of the 94 `svchost` processes hosted at least one, and 5
  hosts held more than one. Windows gives each service its own host on the Client Desktop SKU with
  more than 3.5 GB of RAM, except services marked `SvcHostSplitDisable`
  ([Service host grouping](https://learn.microsoft.com/en-us/windows/application-management/svchost-service-refactoring)).
  Memory in a shared host cannot be divided between its services, so the host is the smallest part
  the picture can size.
- **Parent process identifiers are a hint.** A parent can exit and its identifier be reused, so a
  "parent" created after its child is not the parent
  ([The Old New Thing](https://devblogs.microsoft.com/oldnewthing/20150403-00/?p=44313)). The
  snapshot carries `CreateTime` for every process, so the check costs nothing. On the probe machine,
  458 processes had a live, older parent, 28 had a parent that had exited, and 2 named a parent
  identifier that had been reused.
- **Packaged applications name their package** through `GetPackageFullName`, with only
  `PROCESS_QUERY_LIMITED_INFORMATION`
  ([GetPackageFullName](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getpackagefullname)).
- **"Application" has no documented definition.** Task Manager's split into apps, background
  processes and Windows processes is a heuristic: a visible window makes an app, a critical flag
  makes a Windows process, and anything else is a background process
  ([The Old New Thing](https://devblogs.microsoft.com/oldnewthing/20171219-00/?p=97606)). No
  documented source describes how it folds a browser's many processes into one row; a process tree
  with the creation-time check is the practical approximation.

A tree that follows from these: *Applications* (by process tree, then process), *Services* (by host
process, naming the services it holds), and *Windows* (the compression store, the system cache, the
pools and whatever the picture could not separate). A process with children holds memory of its own
as well as theirs, so its own share is drawn as a child of its own, as a disk picture draws a
folder's loose files.

## 6. Whether closing anything helps

This section decides whether the feature helps a user or misleads one.

- **Free memory being low is normal.** Windows fills otherwise unused memory with cache and, after
  memory is freed, fills it again ([Russinovich, *Inside the Windows Vista Kernel: Part 2*](https://learn.microsoft.com/en-us/previous-versions/technet-magazine/cc162480(v=msdn.10))).
  Standby pages are available the moment something needs them.
- **A large compression store is not a fault.** It grows exactly when memory is being made available
  ([Windows 10 build 10525](https://blogs.windows.com/windows-insider/2015/08/18/announcing-windows-10-insider-preview-build-10525/)).
- **Suspended packaged applications are already handled.** Windows keeps them in memory "unless the
  operating system needs to reclaim resources", and "generally, users don't need to close apps"
  ([App lifecycle](https://learn.microsoft.com/en-us/windows/uwp/launch-resume/app-lifecycle)).
- **The failure a user feels is commit exhaustion.** When commit charge reaches the commit limit
  (physical memory plus page files), allocations fail
  ([Russinovich, *Pushing the Limits of Windows: Virtual Memory*](https://learn.microsoft.com/en-us/archive/blogs/markrussinovich/pushing-the-limits-of-windows-virtual-memory)).
  The other is sustained hard faulting with matching disk load. Closing a program releases its commit
  charge, which is the real and measurable gain.

So the headline is commit charge against the commit limit, with available memory beside it, and the
figure an action reports is commit charge released — before and after, as Storage reports free space.
A picture that led with "used" would present a cache as a problem.

**The trap to stay out of** is the "RAM optimiser". Those tools call `EmptyWorkingSet`, which
"removes as many pages as possible from the working set"
([EmptyWorkingSet](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-emptyworkingset)),
or purge the standby list. The number on screen goes down and the pages come back as hard faults. A
memory view offers neither, in any form.

## 7. Acting

The failure this project exists to prevent is silent, irreversible data loss, and closing a program
can cause exactly that: an unsaved document, a build half-way through, a download. So the action is
the program's own close, which is §5.1's rule restated for this subject, and everything without one
is shown and not offered.

**The program's own close is `WM_CLOSE` to its top-level windows**, the message "sent as a signal
that a window or an application should terminate". The default handling destroys the window, and an
application "can prompt the user for confirmation" first
([WM_CLOSE](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-close)), so unsaved work is the
program's own question, asked in its own words. A program that asks, refuses or keeps a hidden window
is still running when the wait ends, and the view says so and does nothing more.

**A console window is never one of those windows**, whichever process it appears to belong to.
Closing a console sends `CTRL_CLOSE_EVENT` to every process attached to it; the default handler
exits, and a process that handles the event is ended by the system when its handler returns or
after 5 seconds ([HandlerRoutine](https://learn.microsoft.com/en-us/windows/console/handlerroutine)).
The console host reports the console's client process as the window's owner, falling back to the
oldest attached process
([windowio.cpp](https://github.com/microsoft/terminal/blob/main/src/interactivity/win32/windowio.cpp)),
so a search for a process's top-level windows finds its console window too, and posting the close
there ends every attached program without any of them asking about unsaved work. A design never
posts to a console window, and a process whose only top-level window is a console is a console
program. What the hidden window a terminal such as Windows Terminal gives each console does with
`WM_CLOSE` was not checked.

**The Restart Manager is not that route**, though it looks like one. It sends
`WM_QUERYENDSESSION` and `WM_ENDSESSION` with `ENDSESSION_CLOSEAPP`, then `WM_CLOSE` to what is still
running, and `CTRL_C_EVENT` to console programs
([Guidelines for Applications](https://learn.microsoft.com/en-us/windows/win32/rstmgr/guidelines-for-applications)).
It is built for an installer that shuts programs down and restarts them — installers "should always
restart application and services using the RmRestart function"
([RmShutdown](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/nf-restartmanager-rmshutdown)) —
and it asks a program to save what it needs *for a restart* and exit. A program that treats
`WM_ENDSESSION` as the end of the session may exit without asking about unsaved work. That last point
is an inference from the documented contract, not an observed failure, and it is enough to prefer the
user's own close. `RmForceShutdown` terminates what does not respond in 30 seconds, and is never used.

**Refused, with the reason stated:**

| What | Why |
| --- | --- |
| Terminating anything | `TerminateProcess` runs no more of the program's code, so nothing is saved ([TerminateProcess](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess)). |
| A process with no top-level window | There is no close to send it. §5.2's reasoning applies: what has no recognised route is not offered. |
| A console program | It has no close of its own to send. The console control signals another program can raise are Ctrl+C and Ctrl+Break, by attaching to the console ([GenerateConsoleCtrlEvent](https://learn.microsoft.com/en-us/windows/console/generateconsolectrlevent)), and Ctrl+C stops a build rather than closing anything. Closing the console window ends every attached process (above). |
| A service | Stopping needs the `SERVICE_STOP` right, which a service's default security grants only to LocalSystem and Administrators ([Service security and access rights](https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights)), and a service that accepts the stop control; a trigger-start service starts again when its trigger fires ([Service trigger events](https://learn.microsoft.com/en-us/windows/win32/services/service-trigger-events)); disabling one is what "free up RAM" advice gets wrong. The open question in unreached-locations.md is decided first. |
| A suspended packaged application | Windows reclaims it when it needs to (section 6). |
| A critical process | Ending one stops the machine with bug check `CRITICAL_PROCESS_DIED` ([0xEF](https://learn.microsoft.com/en-us/windows-hardware/drivers/debugger/bug-check-0xef--critical-process-died)). `IsProcessCritical` needs only `PROCESS_QUERY_LIMITED_INFORMATION` ([IsProcessCritical](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-isprocesscritical)). |
| A process whose criticality cannot be read | Unelevated, the probe could open none of the twelve system processes it checked by name, and none of the 344 it could open reported itself critical. Elevated was not measured, so the refusal is stated as a rule, not left to access being denied. |
| Another session, another user, Deguffer itself, `explorer.exe`, `dwm.exe` | Not the user's to close from here, or closing them takes the desktop with them. |

**§5.6 is weaker here, and a design would have to say so.** The positive check is exact: the process
with that identifier and creation time has exited, or it has not. The negative is not: processes exit
on their own all the time, and a closed application's own child processes are expected to go with it,
so "nothing else stopped" cannot be proven by comparing two snapshots. What can be asserted is that
Deguffer sent nothing to anything outside the selected process tree, and a design should decide
whether to report other exits in the same window rather than claim none.

## 8. What Deguffer could reuse

Surveyed against `Deguffer.Core/Exploring` and the Explore page.

- **Reusable:** `TreemapLayout`, `IcicleLayout` and `SunburstLayout`, both hit tests, both
  rasterisers and `TilePalette`. Only the three layouts read the tree, and they read four things
  from it: a node's size, its children, whether it is a container, and the tree's child order, which
  the treemap and the sunburst require to be by size. Nothing in them reads a path or a disk. They
  take the concrete `sealed class ExploreTree`, so reuse needs a seam over those four, with the disk
  tree and a memory tree as its two implementations. The hit tests, rasterisers and palette take
  tiles, sectors, colours and integers, and no tree at all. `ExploreSurface`, which chooses each
  node's colour, also reads its parent and its last-written time, so the seam would carry the parent
  as well, and age colouring would not carry over.
- **Not reusable:** `ExploreTree` and `ExploreTreeBuilder` carry a root path and filesystem
  timestamps, and the builder's contract is that a directory's own size is zero, which a process with
  children breaks. `ExplorePlace` matches nodes across rescans by path, and a memory view refreshes
  every second or two and needs identity by process and creation time instead. Age colouring is a
  last-written time. `KnownItem` and `ItemGuide` are keyed by place and path. `ExploreActionPolicy`,
  `ExploreRemover`, `ShellActions` and the Explore selection all act on paths.
- **Precedents:** `RunningProcessTable`'s layout self-check for undocumented structures, the
  `IProcessInspector` seam and its fake for testing process questions without the machine, and the
  "Elevate and rescan" offer.

By G1 and G2, a memory view is its own namespace beside `Exploring`, with its own snapshot source
behind a seam, its own policy and its own action. It shares the drawing code and nothing else.

## 9. What comparable tools do

No mainstream Windows tool found draws per-process memory as a treemap or sunburst; the search was
not exhaustive.

- **Task Manager** lists processes grouped as apps, background processes and Windows processes. Its
  *Efficiency mode* lowers CPU priority and applies EcoQoS, and does nothing for memory
  ([Efficiency mode](https://devblogs.microsoft.com/performance-diagnostics/reduce-process-interference-with-task-manager-efficiency-mode/)).
- **RAMMap** breaks physical memory down by use, by process, by priority and by file
  ([RAMMap](https://learn.microsoft.com/en-us/sysinternals/downloads/rammap)). Third-party accounts
  describe an *Empty* menu for working sets and the standby list; its documentation page does not.
- **VMMap** breaks one process's address space down by type
  ([VMMap](https://learn.microsoft.com/en-us/sysinternals/downloads/vmmap)).
- **System Informer** shows the process tree and offers termination, and warns, from a built-in list
  of Windows processes and the critical flag, that ending one "will shut down the operating system
  immediately" ([actions.c](https://raw.githubusercontent.com/winsiderss/systeminformer/master/SystemInformer/actions.c)).
  Its memory-list commands include emptying working sets and the standby list
  ([memlists.c](https://raw.githubusercontent.com/winsiderss/systeminformer/master/SystemInformer/memlists.c)).
- **Process Explorer** and **Resource Monitor** were surveyed from secondary sources only: a process
  tree with termination, and a single bar of hardware reserved, in use, modified, standby and free.

None of those surveyed pairs a picture with the program's own close as its only action, which is the
part Deguffer's model would add.

## 10. What adopting it would take

In the order the decisions depend on each other:

1. **Whether Deguffer is about more than disk.** A change to §1 and §2, and the maintainer's. If not,
   this document is the answer, and it stays a reference.
2. **A read-only view first**, specified as a new §7 subsection before any code: the three-part tree
   from section 5, sized by private working set, headed by commit charge against the commit limit, with the
   unseparated remainder drawn and labelled. It has value with no action at all.
3. **Whether it acts.** If it does, it sends the program's own close to a windowed application in
   the user's session, never to a console window, refuses everything in the table in section 7 with its reason, identifies the target
   by identifier and creation time, checks again immediately before sending, and reports commit
   charge before and after.
4. **Services stay out** until the open question in unreached-locations.md is decided, and a memory
   view is not a reason to decide it.

If it is adopted, the work splits along the seams in section 8: the specification, the snapshot source and
its tests, the layout seam over the tree, the page, and the close action last.
