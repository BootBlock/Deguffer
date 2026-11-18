# Faster deletion: one open per entry

> **Status:** 🟢 ACTIVE — measured, designed, reviewed and implemented; being verified. Flip to ✅ COMPLETE
> and `git mv` into `done/` when it lands.

## Why

A clean of `C:\Windows.old` took a long time, which read as Deguffer deleting one file at a time.
Two separate things were found.

**`Windows.old` is not deleted by Deguffer's tree removal.** `PreviousWindowsInstallationProvider`
hands it to Windows' own *Previous Installations* Disk Cleanup handler (§5.1, and the
`DiskCleanupStep` remarks for why the folder is never removed by path). The run asks the handler
`GetSpaceUsed`, which walks the whole folder (146 s for 82 GB on one machine, see
`DiskCleanupQuestion.Starts`), then `Purge`, which is Windows' own deletion. Nothing in Deguffer's
removal code runs. See *Out of scope* for what could change there.

**Deguffer's own tree removal is already parallel and already at the volume's limit for an ordinary
file**, except for read-only files and folders, which cost a second open each.

## What was measured

On one 32-thread Windows 11 machine, NTFS on an SSD, unelevated, with real-time antivirus running.
Figures are processor time and elapsed time for empty files, which time what a removal pays per
entry.

| Experiment | Result |
| --- | --- |
| The benchmark's `remove` route, 100,000 files in 1,365 folders | 5.9 s at best, 22.8 s of processor time: about 220 µs per file |
| The same tree, every file read-only (`--read-only`) | 12.1 s, 36.9 s of processor time: the read-only bit is cleared by a second open before each delete |
| 50,000 files, 16 threads: `File.Delete`, `DeleteFileW`, `NtCreateFile` by full path, and `NtCreateFile` relative to the parent's handle | All four within the noise, 160 to 220 µs per file. Relative opens gain nothing, as `DirectoryQuery` already records for listing (#468) |
| 50,000 read-only files, 16 threads: clear then delete, against one open with `FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE` | 5.21 s against 1.88 s |
| 40,000 files at 1, 4, 8, 16, 32 and 64 threads | Flat from 4 to 8 threads, at 22,000 to 27,000 files a second. More threads add processor time, not speed |
| Threads taking files of one folder together, against one folder each | No difference: the shared queue costs nothing |
| `File.Delete` with another handle held open sharing delete | The name is gone at once: `DeleteFileW` already uses POSIX semantics on this build |
| A read-only folder | `Directory.Delete` refuses it. One open with the ignore-read-only disposition deletes it |
| A read-only file held open without sharing delete | The open fails with a sharing violation and the file's attributes are unchanged |
| A folder that still holds a file | The disposition fails with `ERROR_DIR_NOT_EMPTY` (145), the code `DirectoryDeletion` reads |

The volume, not Deguffer, bounds an ordinary file. The one cost Deguffer adds is the second open
per read-only entry. Go marks its whole module cache read-only, and Git marks its objects
read-only, so a real clean meets whole trees of them.

## Design

`HandleDeletion.Delete` already sets `FILE_DISPOSITION_FLAG_DELETE | POSIX_SEMANTICS |
IGNORE_READONLY_ATTRIBUTE` through a handle, and falls back to the older disposition where a file
system such as FAT refuses the extended one. Only the duplicate remover uses it. The tree removal
and `FileRemover` delete through `IFileSystem.DeleteFile` and `DeleteDirectory`, which call
`File.Delete` and `Directory.Delete`.

1. **`EntryDeletion`** (new, `Deguffer.Core/Safety`): deletes one file, or one empty directory, at
   an extended path through one handle, and owns all read-only handling.
   - Opens the path for deletion as `DeleteFileW` opens it: `DELETE | FILE_READ_ATTRIBUTES`, every
     share, `OPEN_EXISTING`, `FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS`, through
     `FileInformation`'s `CreateFileW` declaration. `DeletionProbe` opens through it and drops its
     own, so the `CreateFileW` count falls from 4 files to 3 (`NativeEntryPointTests`).
   - Reads the entry's attributes and reparse tag through the handle
     (`FileInformation.TryAttributeTag`) and routes it with a pure function, `RouteFor`:
     - the wrong kind (a folder or a folder link asked for as a file, a file as a folder) throws what
       `File.Delete` or `Directory.Delete` throws there;
     - a reparse point that is not a name surrogate (a cloud placeholder, a deduplicated file), any
       `IO_REPARSE_TAG_MOUNT_POINT` (a junction or a volume mount point, because the runtime's call
       tells the mount manager), and an entry Windows would not describe go to the runtime's call;
     - everything else, a symbolic link included, goes through the handle with
       `HandleDeletion.TryDeleteNow`.
   - Where the file system does not have the one-call disposition (`HandleDeletion.IsUnsupported`:
     errors 1, 50 and 87), the handle is closed and the runtime's call deletes the entry, as
     `DeleteFileW` falls back.
   - On the runtime route only, a refused read-only entry has its bit cleared for one more try, and
     the bit is set again where that try is refused too. `File.SetAttributes` on a junction changes
     the junction, not its target (observed).
   - Throws the exception the runtime throws for the same Win32 error, type and `HResult` alike:
     a missing file is no error for a file, a missing directory is `DirectoryNotFoundException`,
     access denied is `UnauthorizedAccessException` for a file and a bare `IOException` for a
     directory, and anything else is an `IOException` carrying the code.
2. **`HandleDeletion`** gains `TryDeleteNow` and `IsUnsupported`, and `Delete` (the duplicate
   remover's) is built from them. Error 1 joins its fallback, which also helps the duplicate remover
   on a user-mode file system.
3. **`WindowsFileSystem.DeleteFile` and `DeleteDirectory`** call `EntryDeletion`.
   **`IFileSystem.ClearAttributes`** is removed with every implementation, because nothing outside
   `EntryDeletion` clears a bit any more.
4. **`FileDeletion`, `FileRemover` and `DirectoryDeletion`** lose their read-only retries, and
   `DirectoryDeletion` its emptiness listing. `FileDeletion` loses the attributes parameter.
5. **`Win32Error`** (new, `Deguffer.Core/Safety`): the shared constants type #992 asks for, created
   with the codes this change names. `HandleDeletion`, `DirectoryDeletion` and `EntryDeletion` use
   it, and the `win32-errors-named-once` baseline loses the two files it named. The rest of #992
   stays with #992.

What this does not change: the order a removal tries entries, its bounds, its threads, or §5.6's
checks. A refusal still leaves the entry. On the one-call route it leaves it exactly as it was, and
on the runtime route a refused read-only entry has its bit set again after the retry, where the old
retry left it cleared after every access refusal.

One outcome does change. A read-only folder whose listing was refused but which is empty on disk
was left standing, because the old retry would not clear the bit of a folder it could not list. It
now goes, as a writable one in the same place always did: Windows removes only an empty folder, so
nothing inside it can be lost.

## Tests

Through the real file system in a test's own temporary folder, as the existing removal tests do.

- `EntryDeletion` deletes a read-only file and a read-only empty folder in one call.
- A read-only file held open without sharing delete, and one an access rule refuses, are refused
  and keep their read-only bit.
- A tree removal deletes a read-only file in one seam call (`RemovalWalkTests`, which asserted two).
- Parity with the runtime for each error: missing file, missing parent, a folder asked as a file, a
  file asked as a folder, a missing folder, a full folder (`ERROR_DIR_NOT_EMPTY`), a file held
  without sharing delete, and a folder link asked for as a file. Each asserts the exception type and
  `HResult` the runtime gives for the same case.
- A junction and a directory symbolic link to an **empty** target, read-only or not, go as
  themselves and leave the target (§5.6). An empty target is what makes a followed link succeed and
  fail the test. A file symbolic link goes and leaves its target.
- `RouteFor` routes each kind, including the reparse points a test machine cannot create unelevated.
- A path past `MAX_PATH` is deleted in its extended form. The existing seam tests already assert the
  `\\?\` form that reaches `IFileSystem`.
- Not reachable by a test here: the runtime fallback on a file system without the one-call
  disposition (no FAT volume on the test machine), and the bit being set again after a second
  refusal on the runtime route.

Each new test is proved by mutation. The benchmark's `remove` route is run before and after, with
and without `--read-only`.

## Out of scope

- **The *Previous Installations* handler's own time.** Deguffer could ask it to `Purge` without
  `GetSpaceUsed` and save the measuring walk, but whether `setupcln.dll` purges anything without
  being measured first is not documented and was not observed. A run that purged nothing would
  report the folder left standing, so no data is at risk, but the clean would stop working. It needs
  an observation on a machine with a `Windows.old` to give up first.
- **What the handler leaves behind.** On the machine measured, the handler removed most of
  `Windows.old` and left the previous profile under `Users`, with more than 300 paths longer than
  `MAX_PATH`. Whether those stopped it is not known.
