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
| `Launch` | the process reaches its entry point, `Program.Main`, before the application object is made and its statics read the stores | the main window's first frame after it is activated has been handed to the compositor | start: milliseconds since Windows created the process, so the launch is that much longer than the span between the two events |
| `Window` | inside `Launch`: the main window is about to be built, with the page it opens on | the window is built | none |
| `ToolWindows` | after `Launch`, once the first frame is drawn: a tool window left open last session is about to open again | it is open | none |
| `Show` | the Explore page is about to show a tree | the tree is shown and handed to the map | start: cause, node total. Stop: cause, rows listed, which is 0 while a map is on screen |
| `Selection` | inside `Show`: what is picked is about to be carried to the tree or cleared | done | none |
| `Rows` | inside `Show`: the trail, and the rows while the List layout is on screen, are about to be brought up to date or rebuilt | done | stop: rows listed, which is 0 while a map is on screen |
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

**The launch** is from the moment Windows created the process to the first frame. No event can be
written while the runtime itself starts, so the `Launch` start event comes later and carries how
long the process had existed by then. Everything after it, `App`'s statics reading the stores
(`PreferenceStore.Load` among them) and the XAML parse included, is inside the pair.

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
tree. Use a small folder of invented files. Over four warm launches of one Release build on the
fixture below, launch to first frame ran from 1,304 ms to 1,722 ms and scan end to first map from
192 ms to 230 ms, so compare the medians of three runs or more, not one run with another.

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

For a launch on a fixture of three folders (one holding another) and four files, opened on
Explore, it printed:

```text
Launch to first frame, from the process's creation: 1388.5 ms
  process creation to the entry point (the runtime's start): 89.0 ms
  entry point to OnLaunched (the stores read, the XAML parsed): 339.8 ms
  main window and first page: 633.0 ms

Explore shows (UI thread held), by cause:
  cause     count    median   longest      rows selection   handoff  longest at nodes, rows
  Finished      1   34.8 ms   34.8 ms    4.7 ms    2.7 ms   20.2 ms  8, 0

Map pictures: 1 handed, 1 reached the screen, 0 superseded first, 0 still waiting when the trace ended.
  hand-off to first drawing on screen: median 177.2 ms, longest 177.2 ms, first 177.2 ms

Scan end to first map: 192.1 ms (8 nodes)
```

It gives launch to first frame with its parts (the runtime's start, the application object and its
stores, and the main window), the tool window reopened after it,
each `Show` cause with its count, median and longest time and the parts of the longest, the wait for
each picture, the time from each scan's end to its first map, and the time spent applying duplicate
groups. Its output names nothing from the machine, as the trace does not.

The trace also opens in [PerfView](https://github.com/microsoft/perfview). Its **Events** view lists
every event with its time and payload (filter on `Deguffer-Shell`). Each pair is a task with a
`Start` and a `Stop` opcode, and the pairs nest, so PerfView's activity views show each one as an
activity with its duration.
