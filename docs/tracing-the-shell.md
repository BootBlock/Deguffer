# Timing the shell with a trace

Reference documentation, not a plan: it describes the events the app raises today and how to read
them, and is updated whenever an event is added or changed.

`Deguffer.Benchmark` times the scanner, the file table and the walk. It does not run the window, so
it cannot say how long the window takes to appear or how long a page holds the UI thread. The app
answers those questions itself, through an `EventSource` named **`Deguffer-Shell`**
([`ShellEvents`](../Deguffer.App/Diagnostics/ShellEvents.cs)), which marks each milestone on the UI
thread with a start and a stop event.

Nothing listens to it unless a trace is recording. With no trace, each event costs one check of a
flag, and nothing waits on a frame, so there is no setting to turn it on or off.

**A trace is safe to share.** No event carries a string. The payloads are counts, causes and node
totals, so a trace names no file, folder, drive label or user. A test fails if an event is given a
parameter that is not a number or an enum.

## What is marked

| Pair | Starts | Stops | Payload |
| --- | --- | --- | --- |
| `Launch` | `App.OnLaunched` is called | the main window's first frame after it is activated has been handed to the compositor | none |
| `Window` | inside `Launch`: the main window is about to be built, with the page it opens on | the window is built | none |
| `ToolWindows` | after `Launch`, once the first frame is drawn: a tool window left open last session is about to open again | it is open | none |
| `Show` | the Explore page is about to show a tree | the tree is shown and handed to the map | start: cause, node total. Stop: cause, rows listed |
| `Selection` | inside `Show`: what is picked is about to be carried to the tree or cleared | done | none |
| `Rows` | inside `Show`: the rows are about to be brought up to date or rebuilt | done | stop: rows listed |
| `Handoff` | inside `Show`: the page is about to hand the tree to the map | the map has taken it and asked for its drawing, and the list has marked what is picked again | none |
| `Picture` | the map is handed a new picture (another tree, folder or view) | the first frame after a drawing of it is presented has been handed to the compositor, or the next picture is handed over first | start: picture number, view. Stop: picture number, outcome. Every map in the app counts from one shared number |
| `Groups` | the Duplicates page starts to apply a batch of confirmed groups | the batch and its figures are applied | stop: groups in the batch |

A `Show` cause is `Snapshot` (a partial tree from a scan under way), `Step` (a folder opened or
left, or Back or Forward), `Finished` (the tree a scan ended with) or `Removal` (what is left after a
removal). A `Picture` outcome is `Shown` or `Superseded`. Superseded is usual while a scan sends
snapshots faster than they can be drawn, and for a map hidden behind the List view.

A `Picture` span holds the layout, the paint or the GPU shade (the first shader compile included)
and the copy to the screen. The map draws off the UI thread, so a `Picture` span is time the reader
waits, not time the UI thread is held. The other pairs are time the UI thread is held.

**Time from the end of a scan to the first map** is from the `Show` start with the cause
`Finished` to the stop, with the outcome `Shown`, of the `Picture` that started inside that show.

## Recording a trace

Use [`dotnet-trace`](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-trace), installed
with `dotnet tool install --global dotnet-trace`. Write the trace outside the working tree.

To record from the launch, so the `Launch` pair is in the trace, start the app through it:

```powershell
dotnet-trace collect --providers Deguffer-Shell -o "$env:TEMP\deguffer-shell.nettrace" -- "!Distribution\Deguffer.App.exe"
```

To compare one build with another, add `--explore "--explore-folder=<folder>"` after the
executable. The app opens on Explore already scanning that folder, so every run scans the same
tree. Use a small folder of invented files. The first launch after a build is slower: on the
fixture below it took 2.3 s, and the next two took 1.4 s each, so discard it.

To record an app already running, attach to it by its process ID. There is no `Launch` pair:

```powershell
dotnet-trace collect --providers Deguffer-Shell -o "$env:TEMP\deguffer-shell.nettrace" --process-id <pid>
```

Do what you want to measure, then press Enter or Ctrl+C in the `dotnet-trace` window, or close the
app. Add other providers to the list, such as `Microsoft-Windows-DotNETRuntime`, to see what the
runtime did inside a span.

## Reading a trace

[`scripts/shell-trace.cs`](../scripts/shell-trace.cs) prints each milestone from a trace:

```powershell
dotnet run scripts/shell-trace.cs -- "$env:TEMP\deguffer-shell.nettrace"
```

For the first launch after a build, on a fixture of two folders (one holding a third) and four
files, opened on Explore and then into one folder, it printed:

```text
Launch to first frame: 2254.3 ms
  main window and first page: 1892.4 ms
  tool windows reopened:      0.5 ms

Explore shows (UI thread held), by cause:
  cause     count    median   longest      rows selection   handoff  longest at nodes, rows
  Finished      1   68.9 ms   68.9 ms   17.5 ms    5.2 ms   30.4 ms  8, 3
  Step          1    7.2 ms    7.2 ms    0.9 ms    0.4 ms    4.5 ms  8, 2

Map pictures: 2 handed, 2 reached the screen, 0 superseded first, 0 still waiting when the trace ended.
  hand-off to first drawing on screen: median 180.0 ms, longest 180.0 ms, first 180.0 ms

Scan end to first map: 219.0 ms (8 nodes)
```

It gives launch to first frame with the main window's part of it, the tool window reopened after it, each `Show` cause with its count, median and
longest time and the parts of the longest, the wait for each picture, the time from each scan's end
to its first map, and the time spent applying duplicate groups. Its output names nothing from the
machine, as the trace does not.

The trace also opens in [PerfView](https://github.com/microsoft/perfview). Its **Events** view lists
every event with its time and payload (filter on `Deguffer-Shell`). Each pair is a task with a
`Start` and a `Stop` opcode, and the pairs nest, so PerfView's activity views show each one as an
activity with its duration.
