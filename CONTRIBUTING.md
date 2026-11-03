# Contributing

Thank you for wanting to help. Suggestions, bug reports and ideas are welcome, and they are the
most useful thing anyone can send.

## Pull requests are never accepted

**Deguffer does not accept external pull requests.** A pull request opened by anyone who is not a
collaborator on this repository is closed automatically, without being read. That is not a
judgement on the change or on the person who sent it (but it's gonna be a bot, though, innit?). Deguffer deletes directories on other
people's machines, so every line of it has to be understood and verified by the people who
maintain it, against the safety model in the
[specification](docs/todo/_spec.md). Reviewing a patch to that standard costs more than writing
the change does, so the project does not take patches at all.

## Open an issue instead

Please [open an issue](https://github.com/BootBlock/Deguffer/issues/new/choose) with a short
summary of the change you need. A line or two is enough:

- What you expected Deguffer to do, and what it did instead.
- For a new cache or location, where it lives and what is lost when it is deleted.

That description is what the work actually needs. The code is the easy part.

**Do not include real paths, usernames or machine names.** Redact them to `C:\Users\<user>\...`,
and crop any screenshot that shows them.

For a security vulnerability, follow [SECURITY.md](SECURITY.md) and report it privately. Do not
open a public issue.

## Measuring a scan or a clean

`Deguffer.Benchmark` times the routes a scan takes and the removal a clean makes, so a change to how
Deguffer reads or removes can be judged by a measurement rather than a guess. Every route but
`remove` and `clean` only reads: nothing on the drive is written, moved or deleted. Those two write
and delete only inside a folder they create. It is not part of `dotnet test`, because a timing
changes with whatever else the machine is doing.

Build it in Release, then run one route at a time:

```
dotnet build Deguffer.Benchmark -c Release
cd Deguffer.Benchmark\bin\Release\net10.0-windows10.0.19041.0

Deguffer.Benchmark walk C:\Users\<user>\source --runs 5
Deguffer.Benchmark measure C:\Users\<user>\source --threads 16
Deguffer.Benchmark sources C:\Users\<user>\source --runs 5
Deguffer.Benchmark links C:\Users\<user>\AppData\Local\pnpm\store --read-by handle
Deguffer.Benchmark table C
Deguffer.Benchmark index C
Deguffer.Benchmark explore C
Deguffer.Benchmark explorefolder C:\Users\<user>\Downloads
Deguffer.Benchmark explorewalk C:\Users\<user>\source --snapshots off
Deguffer.Benchmark folders C:\Users\<user>\Pictures D:\Photos
Deguffer.Benchmark remove D:\benchmark --files 100000 --depth 15 --fanout 2 --read-only
Deguffer.Benchmark clean D:\benchmark --trees 500 --files-per-tree 100
```

- `walk <folder>` walks a folder as an unelevated scan does. Run it from an ordinary prompt.
- `measure <folder>` measures a folder as an unelevated scan does: the same walk, and what the
  scan adds up for each entry it lists. Time it beside `walk` on the same folder to see what that
  work costs.
- `explorewalk <folder>` builds the Explore tree for a folder by walking it, as an unelevated
  Explore does. `--snapshots on|off` sets whether it also assembles the pictures of the tree so far
  that Explore draws while it walks. The default is on. Time it both ways to see what they cost.
- `sources <folder>` finds the build outputs in a folder as an unelevated scan does when the folder
  is an approved source folder. It stops at each one it finds and never enters `.git` or
  `node_modules`, so it lists fewer folders than `walk`.
- `links <folder>` measures a folder as a hard-linked store such as pnpm's is measured, reading
  each file's link count and sizes. `--read-by name|handle` sets whether each file is read by
  name, as a scan does where the file system answers that, or through a handle opened on it. The
  default is name. Time it both ways to see what the handles cost.
- `folders <folder> [<folder>...]` scans several folders as a duplicate search does: each volume's
  file table read once where it can be, the walk where it cannot, and the volumes on separate disks
  at the same time. It reads the tables only from an elevated prompt. Time it beside each folder
  alone: the folders' own times add up to what the scan cost when it took the volumes one after
  another.
- `table`, `index`, `explore` and `explorefolder` read a volume's file table, which needs an
  elevated prompt. `table` reads and parses every record and keeps nothing. `index` builds what the
  clean measures locations from. `explore` builds the Explore tree for the whole volume, and
  `explorefolder <folder>` the tree for one folder on it, which still reads the whole table.
- `remove <new folder>` creates the folder, builds a tree of empty files in it before each run, and
  removes the tree as a clean removes a cache or a build folder. It reports the walk, the file pass
  and the folder pass apart, the processor time, and the most worker threads the removal held.
  `--files N`, `--depth N` and `--fanout N` set the tree: how many files it holds (default
  100,000), how many levels of folders it has below its root (default 5), and how many folders
  each folder above the deepest holds (default 4), for at most 1,000,000 folders. `--read-only`
  marks every file read-only, which a removal has to clear before it can delete the file.
- `clean <new folder>` creates the folder, builds `--trees N` projects in it (default 500), each
  with a build folder of `--files-per-tree N` empty files spread over seven folders (default 100), and
  removes the build folders as one plan, as a clean of the `obj` row does. It reports the time per
  step, the use checks, the looks and the work apart, and the removals' passes summed over the
  plan. The use checks ask fakes rather than this machine's running programs, so the result is
  what a step costs with nothing running.
- `remove` and `clean` refuse a folder that already exists, and its parent must exist. After each
  run they check that a file they keep beside the trees, and every project, is still there, and
  stop if one is not. They remove the folder when they finish, even when cancelled. The tree is
  built before each run and is not part of its time.
- `--runs N` runs the route N times. The default is 5.
- `--threads N` and `--listing-buffer KiB` set how many folders the walk lists at once (1 to 64) and
  how many KiB of entries each listing asks Windows for (4 to 1024). Both default to what a scan
  uses, and only `walk`, `measure`, `explorewalk` and `sources` take them.
- `--read-size KiB`, `--reads-in-flight N`, `--parse-threads N` and `--list-reads-in-flight N` set
  how many KiB of records each read of the file table asks for (4 to 16384), how many reads are
  outstanding at once (1 to 32), how many threads parse what arrives (1 to 64), and how many of the
  attribute lists kept outside the table are read at once (1 to 32). They default to what a scan
  uses on a drive of unknown kind, and only the table routes take them.
- Every result states the values it ran with, so compare two results only where those match, or
  where they are the one thing you changed. The values a scan uses for each kind of drive are in
  `AutoTuning`, beside the measurements they come from.

The first run is reported apart from the median of the rest, because they answer different
questions. The walk's first run lists folders from the drive, and later runs list them largely from
the Windows file cache. The file table is read without the cache, so its later runs mostly measure
the drive again. The peak working set for the later runs is the highest the process reached. The
Explore routes also report what the finished tree keeps: the managed heap with the tree held, less
the heap once it is let go, which the peak working set cannot show. "Processors busy" is the
process's processor time over the elapsed time: a run that waits on the drive keeps few busy, and
one that waits on its own work keeps about as many busy as it has threads doing that work.
The table routes also report how many reads each run made, how many records `$MFT`'s bitmap marks in
use, counted once before the first run, and the bytes of free records each run read beyond those:
what reading across free records cost the run. That last figure holds only for a complete run.
They also report how many reads of clusters each run made, the bitmap's and those of the attribute
lists kept outside the table, and how long the lists took from the first read starting to the last
ending: the time the list reads add after the first pass.

To report a result, open an issue and paste the output as it stands. It names the drive letter and
the file system, and never a path, a volume label, a user or a machine. Add the kind of drive
(NVMe, SATA SSD, spinning disk, network share) and whether anything else was running.
