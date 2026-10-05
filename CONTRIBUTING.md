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

## Measuring a scan

`Deguffer.Benchmark` times the routes a scan takes, so a change to how Deguffer reads the disk can
be judged by a measurement rather than a guess. It only reads: nothing on the drive is written,
moved or deleted. It is not part of `dotnet test`, because a timing changes with whatever else the
machine is doing.

Build it in Release, then run one route at a time:

```
dotnet build Deguffer.Benchmark -c Release
cd Deguffer.Benchmark\bin\Release\net10.0-windows10.0.19041.0

Deguffer.Benchmark walk C:\Users\<user>\source --runs 5
Deguffer.Benchmark table C
Deguffer.Benchmark index C
Deguffer.Benchmark explore C
```

- `walk <folder>` walks a folder as an unelevated scan does. Run it from an ordinary prompt.
- `table`, `index` and `explore` read a volume's file table, which needs an elevated prompt.
  `table` reads and parses every record and keeps nothing. `index` builds what the clean measures
  locations from. `explore` builds the Explore tree for the whole volume.
- `--runs N` runs the route N times. The default is 5.
- `--threads N` and `--listing-buffer KiB` set how many folders the walk lists at once (1 to 64) and
  how many KiB of entries each listing asks Windows for (4 to 1024). Both default to what a scan
  uses, and only the walk takes them.
- `--read-size KiB`, `--reads-in-flight N` and `--parse-threads N` set how many KiB of records each
  read of the file table asks for (4 to 16384), how many reads are outstanding at once (1 to 32),
  and how many threads parse what arrives (1 to 64). They default to what a scan uses on a drive of
  unknown kind, and only the table routes take them.
- Every result states the values it ran with, so compare two results only where those match, or
  where they are the one thing you changed. The values a scan uses for each kind of drive are in
  `AutoTuning`, beside the measurements they come from.

The first run is reported apart from the median of the rest, because they answer different
questions. The walk's first run lists folders from the drive, and later runs list them largely from
the Windows file cache. The file table is read without the cache, so its later runs mostly measure
the drive again. The peak working set for the later runs is the highest the process reached.

To report a result, open an issue and paste the output as it stands. It names the drive letter and
the file system, and never a path, a volume label, a user or a machine. Add the kind of drive
(NVMe, SATA SSD, spinning disk, network share) and whether anything else was running.
