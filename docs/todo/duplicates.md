# Duplicates — build plan

> **Status:** 🟢 ACTIVE — the build plan for [_spec.md §7.4](_spec.md#74-duplicates--the-files-that-are-there-twice),
> tracked by [#297](https://github.com/BootBlock/Deguffer/issues/297). Phase 0 (the spec and this
> plan) is done; the status log says which later phases have landed. The spec decides what is
> built; this file records the research it rests on, the order it is built in, and what each phase
> landed.

Duplicates finds files that are on the disk more than once, across one drive or several, matched on
the name, size, last-modified time or content the user chooses, and removes the copies the user
marks while every group keeps at least one copy. Until #297 the spec listed "not a duplicate finder"
among its non-goals. §2 was amended in the same change as §7.4, and the reason for the reversal is
the research below: each way the established tools have lost their users' data has a rule in §7.4
written against it.

## What users want, and how they lose data

Researched on 2026-10-08 from the documentation and issue trackers of dupeGuru, Czkawka, fclones,
jdupes, fdupes, rmlint, Duplicate Cleaner, AllDup, Auslogics Duplicate File Finder, CCleaner,
TreeSize, WizTree, Everything, SearchMyFiles and Duplicate File Detective, and from vendor forums
and Microsoft Q&A. The ranking is by how often and how strongly a need
recurs across those sources.

| Need | Evidence | Where §7.4 meets it |
| --- | --- | --- |
| Never lose the last copy | Duplicate Cleaner's "groups with all files marked" check; AllDup's "do not process groups where all files are selected"; SearchMyFiles' warning; dupeGuru's reference file that can never be marked | Every group keeps a copy that can be kept, enforced in Core twice |
| Reference locations ("delete only what is already in my master") | dupeGuru #296 (56 comments); Czkawka #1643, #1238; rmlint `-k`/`-m`; fclones `--keep-path` | A location can be a reference, never marked |
| Recoverable removal | Czkawka #693 "trash should be the default"; Auslogics Rescue Center | Recycle Bin by default, never an outright fallback |
| Groups sorted by wasted space, files grouped by folder | Czkawka #422; WizTree's Dup Count/Dup Size columns | Groups sorted by the space they could free |
| Rule-based marking | dupeGuru #736 (40,000 files); Czkawka #1324, #903; dupeGuru #1148 | Named rules, run by the user, nothing marked on arrival |
| Combinable criteria | Czkawka #298: two different videos of exactly the same size | Name, size, modified time and content, in any combination |
| A choice of checksum | fclones #153 (regulation, paranoia); Czkawka #1632 (reuse a value from elsewhere) | Seven algorithms, the value shown and exported |
| Speed and a checksum cache | fclones' benchmark (1.46 M files, 316 GB, NVMe: fclones 34.6 s, czkawka 2 min 9 s, jdupes 5 min 2 s); caches in dupeGuru, Duplicate Cleaner, AllDup, fclones, Czkawka | Staged matching, readers bounded per disk, a cache keyed by file ID |
| Empty files left out | jdupes ignores them unless `-z`; dupeGuru #1283 | Empty files are never matched |
| Export | fclones, rmlint, SearchMyFiles, dupeGuru | CSV, written only where the user says |
| Whole duplicate folders | Czkawka #676, #976, #1182; fclones #51; dupeGuru #1195 warns that bundle-like folders must not be split | Not built: shown as files (§7.4, "does not authorise") |
| Similar images and audio | dupeGuru #560, #345; Czkawka's similar-media tools | Not built (§2) |

| How data was lost | Source | The §7.4 rule written against it |
| --- | --- | --- |
| Every copy in a group selected | Duplicate Cleaner, AllDup manuals | Every group keeps a copy that can be kept |
| A file listed as its own duplicate when a folder was named twice | fdupes man page | Locations resolved before enumeration; identity by file ID; no network locations |
| Two paths differing only by case, both deleted when one was chosen | Czkawka #417 (open) | A path is never the key, in matching or in §5.6 |
| A failed link or replace that deleted the file anyway | Czkawka #1498, #1656, #1991; Duplicate Cleaner 3.2.5 | No replacement by a link |
| Files shown as equal with different checksums, from a cache fault | dupeGuru #1015 | A byte comparison before every removal |
| A sampled checksum taken as the verdict | dupeGuru #908; jdupes `-Q` warning | A checksum groups and never licenses a removal |
| A name-and-size match taken as identity | WizTree's own warning; Czkawka #2019 (embedded cover art) | A name or size group says its files may differ, and needs the bytes to match |
| A program's private DLL deleted | CCleaner's advice; Microsoft Q&A | Program folders, from the installed programs' entries and `%LOCALAPPDATA%\Programs`, skipped and refused |
| A catalogue (Lightroom) broken by removing files it names | Adobe community | The confirmation lists every copy that goes |
| A cloud placeholder downloaded by hashing, or the cloud copy deleted everywhere | Microsoft's attribute documentation; OneDrive support | Never read a file not on this device, checked again on the open file; a cloud folder says what removal does there |
| Hard links reported as duplicates, so removal freed nothing | TreeSize "ignore NTFS hardlinks"; dupeGuru #364, #1388 (inode without device) | One file by volume and file ID; never counted, never offered |
| A symbolic link kept while its target was deleted | fdupes, jdupes man pages | Links are never followed and never matched |
| A file changed or moved between search and removal | dupeGuru #72; rmlint's re-check; fclones' cache warning | Identity, size, time, attributes, bytes and streams checked again, with the kept copy held open |
| Long paths missed, or not taken by the Recycle Bin | dupeGuru #1204; Duplicate Cleaner changelog | §6.3 throughout; a path the bin refuses fails |
| `$Recycle.Bin` contents matched as duplicates | voidtools forum | Every `$Recycle.Bin` is skipped and refused |
| A reference folder that became deletable | dupeGuru #1073 (stale saved state) | A reference is never marked, decided again before removal |

Three more ways to lose data are not in the tools' records, and §7.4 answers each: a kept copy that a later Storage clean removes (in `%TEMP%` or a
provider's target), a copy whose named streams hold data its match lacks, and a large removal that
makes Windows empty the oldest items of a full Recycle Bin.

## Technical facts the design rests on

Gathered on 2026-10-08 from Microsoft Learn, the .NET API documentation, the source of fclones and
jdupes, and this repository. **(unverified)** marks a claim that a phase must measure before
relying on it. Where a phase finds a fact here wrong, it corrects this section in the same change.

**The platform**

- **System.IO.Hashing is a NuGet package, not part of .NET 10**: version 10.0.12, MIT, from
  Microsoft, no dependencies on `net8.0` and later. It provides `XxHash128`, `XxHash3`, `XxHash64`,
  `Crc32` and `Crc64` behind `NonCryptographicHashAlgorithm`. Measured in phase 3: `Crc32` writes
  its value least significant byte first, so its bytes are reversed to print `cbf43926` for
  "123456789" as other tools do, while `XxHash128` already writes the canonical order `xxh128sum`
  prints. It is the first package
  `Deguffer.Core` references, and the only one this plan adds. The App ships untrimmed (see
  [the AOT evaluation](aot-and-single-file-evaluation.md)), so trimming does not apply. BLAKE3 is
  not offered, because its maintained .NET port would be a second dependency for the role XXH128
  already fills.
- **Cryptographic checksums come from Windows (CNG)** through `System.Security.Cryptography`. MD5,
  SHA-1 and the SHA-2 family work on every supported Windows; SHA3-256 needs Windows 11 build 25324
  or later, so it is offered only where `SHA3_256.IsSupported`.
- **Throughput.** Measured in phase 3 on an i9-13900K, one thread hashing in memory: XXH128
  54.6 GB/s, CRC-32 27.5, SHA-256 2.61, SHA-1 0.92, SHA-512 and MD5 0.74, SHA3-256 0.56. An NVMe
  SSD read with unbuffered, aligned 1 MiB reads of 64 files of 64 MiB levelled off near 3.3 GB/s,
  reached by XXH128 with two readers and by SHA-256 with four; eight or sixteen added nothing, so
  four readers is a solid-state disk's bound. fclones' author reports that the choice of checksum barely
  matters except on a fast SSD or cached data, because the disk sets the pace, and that several
  readers on one spinning disk are much slower than one.
- **Reading.** Corrected in phase 3: the content is opened by the file's number with
  `OpenFileById` (`ExtendedFileIdType` with the 128-bit ID, `FileIdType` with the 64-bit one on the
  older route), using the attributes-only handle it was described through as the volume hint, and
  `RandomAccess.Read(handle, span, offset)` reads the first and last blocks and the full content
  without a shared position. A path opened by `File.OpenHandle` is walked again and follows every
  link on it, its own name included, so a name replaced by a link after the description would send
  the open to another file, a share, or a cloud file that opening recalls; an open by number walks
  no path. While the described handle is held, Windows refuses to rename any folder above the file,
  so only the file's own name can be replaced. Where a volume will not open a file by number, the
  path is opened only where the volume answered that it supports no reparse points
  (`LocalVolume.CannotHoldLinks`; a volume that refused the question may be NTFS and never
  qualifies) and the path starts at that volume's own drive letter, since a volume reached through
  a folder of another is reached through folders that can be links. Anywhere else the file is a
  failed read. Which error a FAT or exFAT driver gives is **(unverified)**: no such volume was
  attached, and the rule does not depend on it.
- **Identity.** `GetFileInformationByHandleEx(FileIdInfo)` returns the volume serial and the 128-bit
  file ID, which ReFS needs because its 64-bit index is not unique. A handle opened for
  `FILE_READ_ATTRIBUTES` with `FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS` reads it
  without following a link or recalling a cloud file. On NTFS, measured on 2026-10-09, it gives the
  64-bit serial while the older `GetFileInformationByHandle` gives only its low 32 bits, so a file
  identified once by each would read as two files. `FindFirstFileNameW` lists every name of a file,
  each from the top of its volume (`GetVolumePathNameW`), and `FindFirstStreamW` every named stream;
  `GetFileInformationByHandleEx(FileStreamInfo)` lists the streams through a handle already held,
  one opened for attributes alone included (measured in phase 5), and `NtCreateFile` opens a named
  stream relative to that handle, given the stream's name alone, so no path is walked to read it. On FAT and exFAT
  a file ID can change after a defragmentation or a rename, so it identifies a file for one search
  and never for the cache. Whether `FileIdInfo` or the older call answers on FAT and exFAT is
  **(unverified)**: measuring it needs a scratch disk attached with administrator rights, and phase 2
  ran without them, so the search decides it for each volume as it runs (phase 2, step 5).
- **Change time.** Corrected in phase 8: nothing read a file's change time before, though the
  `FileBasicInfo` the description already read holds it (`FileDescription.Changed`). Measured on
  NTFS: a rewrite whose last-modified time is put back, a change to the file's security, and a
  rename each move it, so it is what tells a cached checksum it is still the file's, and a renamed
  file is read again. A program can set it back on purpose, as it can the other times.
- **Cloud files.** Reading the data of a file marked `RECALL_ON_DATA_ACCESS` downloads it, and no
  documented call lets a process read it without that. `RtlSetProcessPlaceholderCompatibilityMode`
  changes how a placeholder looks, not whether it downloads. Measured in phase 3: an unpackaged
  process's default is `PHCM_DISGUISE_PLACEHOLDER` (1), which its threads follow, and through it an
  attributes-only handle on an online-only placeholder shows no reparse point and still shows
  `RECALL_ON_DATA_ACCESS`, so the check before a read needs no mode set; `CloudFiles` sets the
  thread's mode where it needs to see placeholders themselves. Opening a `RECALL_ON_OPEN` file for its content can
  download it before any check on that handle runs, so the check uses an attributes-only handle
  first. A fully downloaded OneDrive file keeps
  its cloud reparse tag, so the tag is no sign of a file being online-only; the attributes are.
  Deleting an online-only OneDrive file deletes it everywhere; the OneDrive recycle bin keeps it for
  30 days (personal) or 93 (work or school).
- **Space.** ReFS block cloning, which Windows 11 24H2 uses for ordinary copies on a Dev Drive, and
  Windows Server deduplication (`IO_REPARSE_TAG_DEDUP`) let copies share clusters. No user-mode API
  reports what a cloned file shares **(unverified)**, so there a removal can free less than the
  figure, which counts shared clusters in full.
- **Recycle Bin.** When a bin is full, Windows deletes its oldest items outright to make room for
  new ones. Measured in phase 4: `SHQueryRecycleBin` answers what this account's bin on a volume
  holds as the sum of its items' lengths, and takes a drive's top, a folder on it, the extended form
  of either, or the volume's `\?\Volume{GUID}\` name alike; the limit is the `MaxCapacity` number,
  in megabytes, under the account's `Explorer\BitBucket\Volume\{GUID}` key, with `NukeOnDelete`
  beside it for a bin set to keep nothing (`RecycleBinRooms`). The measurement ran in a 64-bit
  process, where `SHQUERYRBINFO` is 24 bytes with the size at offset 8; the 20-byte layout a 32-bit
  process uses, packed as the SDK header packs it there, is **(unverified)**. Measured in phase 5:
  `IFileOperation`'s progress sink hands the item it put in the bin to `PostDeleteItem`, its path in
  the account's `$Recycle.Bin` folder on the same volume, with the file ID the file had before,
  because the move is a rename on that volume (`BinnedItem`); and the shell moves a file that another
  handle holds, as long as that handle shares reading and deleting.
- **Deleting through a handle.** Corrected in phase 5, measured on NTFS: `SetFileInformationByHandle`
  with `FileDispositionInfoEx` deletes the file a handle holds, whatever its path names by then, but
  only through a handle opened by path. Through one opened by its number with `OpenFileById` it is
  refused with `ERROR_INVALID_PARAMETER`. POSIX semantics take the name away as the handle closes,
  and `FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE` deletes a read-only file without changing its
  attributes first (`HandleDeletion`).
- **Holding a file open.** Measured in phase 5 on NTFS: a handle opened by number that shares only
  reading refuses another program's write, and does not refuse a rename or a delete; one opened by
  path refuses all three. So a file that must stay is held by its path, never its number. A folder
  can be made case-sensitive without elevation (`FileCaseSensitiveInfo`), which is how a test builds
  two files whose names differ only in case.

**This repository**

- **The scan.** `IExploreScanner.ScanAsync` scans one root, by the file table when elevated and by
  a bounded walk otherwise, into an `ExploreTree`. Each call reads the whole file table again, so
  `ExploreScanner.ScanFoldersAsync` (phase 1) reads each volume's table once, rooted at the
  volume's top, and finds each folder in it by its exact name, walking a folder the table cannot
  answer for or holds only in another case. Each `ScannedFolder` carries its route's note, and
  says whether it came from a table that was not read whole or held a record the read could not
  place (`MftExploreRead.EveryRecordRead`); the walk names each folder Windows refused to list
  (`ExploreTree.ListingWasRefused`). The tree
  holds names, parents, sizes on disk, lengths, storage, times and, since phase 1, whether an entry
  is hidden or a system file (`FileVisibility`); it holds no file IDs or change times. Its times are
  whole minutes (`ExploreTimestamp`), so they never decide a match or a re-check. The file-table
  route keeps one name per record (`MftExploreReader`), so the tree cannot list a file's hard links;
  the walk sees each name.
- **Cloud state.** `FileStorage.CloudOnly` marks a file `OFFLINE`, `RECALL_ON_OPEN` or
  `RECALL_ON_DATA_ACCESS` on both routes. It is the state at scan time.
- **Disks.** `MediaClassifier` reads each disk's seek penalty and folds it into a volume's
  `StorageMedia`, which `ScanTuner.MediaOf` returns. `VolumeMedia.PhysicalDisks`, reached through
  `VolumeMediaCache.Of`, says which disks a volume lies on, which is how two volumes on one disk
  share one reader.
- **Volumes.** `IVolumeInventory`'s `IsLocalDisk` is true for a fixed or removable drive that does
  not store its content remotely, which leaves out a cloud client's drive that Windows reports as
  fixed. It takes `DriveType` at face value, and most USB disks report `Fixed`; `StorageMedia`
  classes a disk by its bus, and its `Removable` covers USB, SD, MMC and FireWire, so that is what
  decides whether a drive is internal. Since phase 8 a volume also names its file system, from the
  same `GetVolumeInformation` call (`LocalVolume.FileSystem`), and only NTFS and ReFS keep a file's
  number with the file (`LocalVolume.KeepsFileNumbers`).
- **Cloud folders.** `ICloudFiles.SyncRoots` lists the roots registered with the Cloud Files API and
  returns null where Windows will not list them. It leaves out a root Windows will not hand back,
  such as one under `AppData\Local`, and a client that does not register at all.
- **Provider targets.** Corrected in phase 4: `ICleanupProvider.ToolRoots` and
  `DiscoverToolRootsAsync` declare what Explore refuses beside a cache, not where a clean deletes.
  A sweep of every provider found cleans that delete where no root is declared (the temporary
  folders, crash dumps, build output under the approved source folders, a cache a tool reports
  elsewhere, Windows' own servicing folders) and roots that recognise every child or none, so the
  roots answer neither way. Every provider now declares where its clean deletes, without planning
  (`ICleanupProvider.CleanedPlacesAsync`, `CleanedPlace`), and each provider's tests check that the
  places cover every path its plan cleans (`CleanedPlaceCoverage`).
- **Paths.** `LongPath.Canonical` normalises a path's form and expands 8.3 names; it resolves no
  link and no drive letter. `ReachedFolder.At` gives every place a folder is reachable at, through
  substituted drives and volumes mounted in folders. `FileInformation.FinalPath` (phase 1) follows
  every link on the way to a path, its own name included, and a substituted letter, to the path an
  opened handle gives, and is the one declaration of `GetFinalPathNameByHandle`: it moved there from
  `CloudFilesNative`, whose `CloudFiles.Resolve` already followed every link and now calls it.
- **Size on disk.** The attributes-only handle that identifies a file already reads
  `FILE_STANDARD_INFO`, whose `AllocationSize` is what the file occupies (`FileDescription.Allocated`,
  `DuplicateCandidate.SizeOnDisk`, phase 4): less than the length for a compressed or sparse file,
  nothing for one held in its file record. A sum of them is what the copies occupy, which a removal
  may free less than: shared clusters are counted in full, and the Recycle Bin frees what it holds
  only when it is emptied.
- **File information.** `FileInformation` (phase 2) is the one declaration of each call that
  describes a file through a handle, and of the attributes-only handle itself (`FileInformation.Open`):
  `HardLinkAwareScanner`, `CloudFiles` and `OccupancyProbe` read through it, where before the first
  two each declared their own `GetFileInformationByHandleEx`. `FileInformation.Describe` answers
  identified, gone or unreadable.
- **Refusals.** `ExploreActionPolicy.MayRemove` decides one path. Asked of a folder, it also refuses
  a folder that *holds* something refused (`HeldLocations`), so it cannot serve as a skip list
  (`C:\Users` would be skipped). `ExploreActionPolicy.RefusedAtAndBelow` (phase 1) answers for a
  place and everything in it: what Windows and NTFS keep at the top of a volume and every
  `$Recycle.Bin` (`TopOfVolumeRefusals`), Outlook's mail stores and its `Outlook Files` folder, and
  each `PathAndBelow` region less what a `Permitting` region inside the place carves out, so
  `C:\Users` and the signed-in profile are not refused and another account's profile is. `MayRemove`
  asks the same members first. Its refusal reasons name Explore.
- **Install locations.** `InstallLocation.Of` accepts any fully qualified path an entry gives,
  including a drive's top or a profile.
- **Removal.** `ExploreRemover.RemoveAsync` partitions the whole batch by the policy once, then
  removes each item with no hook between items. Its §5.6 check compared sibling paths ignoring case
  until phase 5 moved it into `SiblingCheck`, which both removers share and which compares exact
  names. `ShellRecycleBin` uses `IFileOperation` with `FOFX_RECYCLEONDELETE`, so a file the bin
  cannot take fails rather than going outright, and it refuses a path the shell cannot parse.
- **Elevation.** `ElevationRequest` has `Preview`, `InstalledApps` and `ExploreRequest`, each a
  switch on the command line with a round-trip test, and since phase 6 `DuplicatesRequest`, which
  carries each location as its own argument with its role in its switch.
- **Ordering the groups.** Corrected in phase 6: the space a group could free needs the keeping
  rule, and the keeping rule needs the program folders that only finding the candidates reads, so
  before phase 6 the groups could be sorted only once the search had ended (`DuplicateMarks` took
  the whole result). `DuplicateSearcher.SearchAsync` now hands the candidates to a caller, and waits
  for it, before any content is read or any group confirmed; `DuplicateSearchRun` makes the marks
  then, from the finding (`DuplicateMarks.ForAsync(CandidateFinding, …)`), and each confirmed group
  is placed by `DuplicateMarks.Add` and never moved. Every copy a group can hold is a candidate, so
  each candidate's drive is asked once, before the first group.

## How it is built

Core holds every decision, in a new `Deguffer.Core.Duplicates` namespace. The page only wires them:
a choice the page makes is a decision, and a decision is tested in Core rather than in a
view-model. Each phase lands on `main` on its own, with its tests, and leaves a tree that builds and
passes. Every seam that reaches the file system takes its path through `LongPath`, and each phase
that adds one asserts the `\\?\` form of the path that reaches Win32, because a deep-tree test passes
on a machine with long paths enabled whether or not the seam handles them.

Each fact marked **(unverified)** above is measured in the phase that first relies on it, and the
measurement replaces the mark here.

### Phase 1 — The search and its locations (Core)

1. **The search, as values.** `MatchCriteria` (flags: name, size, modified, content),
   `ChecksumAlgorithm`, `SearchLocation` with a `LocationRole` (search or reference), the size and
   extension filters, the hidden and system switches, and whether skipped places are included, in
   one `DuplicateSearch` record that refuses an empty criteria set or no location.
2. **Locations resolved.** Each location's folder is resolved to where it is (`SearchLocations`): a
   substituted letter is followed through `IVolumeInventory`, then the folder is opened and its
   final path taken (`FileInformation.FinalPath`), which follows a junction or symbolic link
   anywhere in it and names a folder-mounted volume by its drive letter, or by its mount point where
   Windows gives its GUID. `FileInformation` is an instance with an internal constructor taking
   the call that opens the handle, so a test sees the path reach Windows in its `\\?\` form; a
   folder deeper than `MAX_PATH` opens without the prefix on a machine that allows long paths, so
   only the form can show it. Two locations are one folder where their final paths are equal, or where
   `ReachedFolder.At` finds one folder at two mounts of one volume. Whether one location holds
   another is asked of the final paths ordinally, because a case-sensitive folder can hold `Photos`
   and `photos`, and only of two locations on one volume: a volume with no letter is named by the
   folder it is mounted at, the enumeration of the drive holding that folder does not cross the
   mount point, so a location on another volume is enumerated in its own right. A share, a letter
   the inventory calls a network drive and a drive that keeps its files in the cloud are refused
   before anything is opened on them, because opening is itself a conversation with another
   machine or the cloud client; any other drive waits for the final path, because a link on it can
   lead to a local disk. The final path is then asked again, because a link can lead to a share or
   to a volume that is not `IsLocalDisk`, which rules out network drives and a cloud client's drive.
   A location Windows will not open is reported as one it could not open, never as absent. A file's
   role is that of the innermost location holding it, a folder given in both roles is a reference,
   and a folder that matches a location only when case is ignored takes the reference role where
   either role is one. A reference location that is not resolved still names a place, its path once
   a substituted drive is followed, and a location holding that place passes over it with
   everything in it, matching it ignoring case, and names it with its reason
   (`UnresolvedReferences`), because its files would otherwise take the holding location's role; an
   unresolved location to search changes nothing.
3. **Refused at and below.** `ExploreActionPolicy.RefusedAtAndBelow` answers whether a place and
   everything in it is refused, taking away what a `Permitting` entry carves back out, and
   `MayRemove` asks the same members first, so neither keeps a copy of the rules. A mail store is
   refused by its type, so a folder named like one is passed over with its contents, which errs, as
   the rule does, on the side of the mail. `RefusalWatch` says which children can be refused where
   their folder was not (the top of a volume, a name Outlook's rules refuse, and the folders on the
   way to a region's folder), so a search asks the policy about a handful of folders rather than
   each; a test asks it about every file and folder of a tree and gets the same answer. Program
   folders join the skip list here (`ProgramFolders`): each installed program's install location,
   read from the keys §7.3 reads with `InstallLocation.Of`, and `%LOCALAPPDATA%\Programs` through
   `IUserEnvironment`. An install location that `StandingFolders` refuses as a target (a drive's
   top, a folder holding the profile, the profile's application data, a Windows or program folder,
   or one of the user's own folders), another account's profile, or a folder holding a chosen
   location is set aside and reported, as is a list of programs Windows would not read. The search
   reports each place it passed over with its reason (`CandidateFinding`).
4. **Enumeration.** `ExploreScanner.ScanFoldersAsync` reads each volume's file table once, rooted at
   the volume's top, and finds each location in it by its exact name, and walks a location the table
   cannot answer for or holds only in another case, so the choice of route stays in
   `ExploreScanner`. `CandidateWalk` applies the places passed over, links, empty files, hidden and
   system files, and the size and extension filters as it reads the tree, and builds no path until a
   file has a match. It names each folder Windows refused to list and each location read from a
   table that was not read whole or held a record the read could not place
   (`CandidateFinding.Unread`), so a search that skipped a place never reads as one that found
   nothing there; a refused folder inside a place passed over is named once, as passed over. Each
   location read carries the note on the route it was read by (`CandidateFinding.Read`). The scanner
   still reads a passed-over place's entries on the walk; phase 3 measures whether passing over them
   in the walk itself is worth its cost. **Decided:** hidden and system attributes became a column
   on both routes (`FileVisibility`, one byte an entry; by arithmetic rather than measurement, about
   1 MB for each million records of the file table), because neither route reads
   anything more for it: the file table takes the attributes from the `$STANDARD_INFORMATION` it
   already reads the dates from, and the walk's listing hands them over. The switches apply to
   files, so a visible file in a hidden folder is searched.
5. **Grouping by length and name.** Candidates are grouped by length where the size or the content
   is a criterion, and by name (ordinal, ignoring case) where the name is, so a name-only search
   groups files of any length; singletons are dropped; cloud-only files are kept only where content
   is not a criterion, and counted where they are left out. A file whose length the scan could not
   establish is counted apart from an empty one. The modified-time criterion waits for phase 2's
   full-precision times, so where it is the only criterion every file kept is one group until then.

Proves: a search with no criteria or no location is refused; two locations reaching one folder
through a substituted drive, a mounted volume or a junction are enumerated once; a location on a
volume mounted inside another is enumerated in its own right; a reference inside a searched drive
stays a reference, and a folder in both roles is a reference; a reference Windows will not open is
passed over by a location holding it, and so is a folder given in both roles whose reference will
not open; a network location and a remotely
stored drive are refused, a mapped network drive before anything is opened on it; a folder the table
holds only in another case is walked; a folder Windows will not list and a location read from an
incomplete table are named; every default skip comes from the shared refusal member and is reported,
including a program's install location and other accounts' profiles; `C:\Users` and the signed-in
profile are not skipped; an install location naming a drive's top, a profile or Documents is set
aside and reported; each filter excludes exactly what it names, at its boundaries; an empty file and
a link never reach a group, while a deduplicated, compressed or cloud file does; names group
ignoring case.

### Phase 2 — Identity (Core)

1. **One file-information seam.** It completes the seam phase 1 began, over `FileIdInfo`,
   `FileStandardInfo`, `FileBasicInfo` and `FileAttributeTagInfo`, opened for attributes only. It
   answers *identified*, *gone* or *unreadable*, and never reads a refusal as absence: a file
   Windows refused to describe may still be there, and reading it as gone would let its copy be
   removed. `HardLinkAwareScanner` and `CloudFilesNative` move onto it, so the declarations are not
   duplicated a third time.
2. **Names.** Every name of a file with several, through `FindFirstFileNameW`, on both routes. Its
   named streams are not listed here: nothing in a search reads them, and the comparison before a
   removal lists them through the handle it holds (phase 5), so a list taken by path at search time
   would answer for whatever is at the path by then.
3. **One file is one file.** Candidates with one identity become one file with all its names, never a
   group, which is the end-to-end proof that a file is searched once however it was reached. A file
   with several names is never marked and never counted, and it can still be the copy a group keeps.
   A file reached once in a reference location is a reference.
4. **Full-precision times.** The modified-time criterion and every later check use the identity's
   times, never the tree's. The tree groups first by its minute, which both routes truncate the same
   time to, so only files that may match are opened; a file the tree holds no time for is identified
   first and placed by the minute Windows gives.
5. **FAT and exFAT.** Not measured: attaching a scratch FAT disk needs administrator rights, and
   phase 9's verification, which runs elevated, measures it. The search decides it for each volume
   as it runs instead: `FileIdInfo` where the volume answers it, else the older call, never a mix on
   one volume, and where neither answers, the volume is not searched and each location on it is
   named.

Proves: hard links are one file on both scan routes, with every name; two locations that alias one
folder yield each file once; a refusal is never read as absence; the times compare to the tick; a
volume without identities is reported and not searched; the path reaching Win32 is in its `\\?\`
form.

### Phase 3 — Content (Core)

1. **Checksums.** One abstraction over `NonCryptographicHashAlgorithm` and `IncrementalHash`, an
   implementation per family, and SHA3-256 offered only where supported. The managed `XxHash128`
   and SHA-256 throughput is measured here, and the figures set the per-disk bound in step 3.
2. **Reading.** A content seam that first reads the attributes through phase 2's attributes-only
   handle and refuses an online-only file there, whatever the tree said, and only then opens the
   content by its file ID through that held handle and reads it with `RandomAccess`. The default
   placeholder mode an unpackaged Deguffer sees is measured here.
3. **Staged matching.** First and last blocks, then the full content, each stage only over what the
   last left. Readers are bounded per physical disk from `VolumeMediaCache.Of`: one on a disk with
   a seek penalty, the measured bound on one without, and one where the media is unknown or
   removable. Every path is cancellable. A read that fails, or a file whose identity, size or time
   changed while it was read, leaves the file out and is counted.
4. **The searcher.** One orchestrator that holds no matching rule, which runs phases 1 to 3,
   reports progress by stage, and streams each `DuplicateGroup` as it is confirmed. A group carries
   its criteria, its checksum and algorithm where content matched, and its files with their roles,
   storage and identities. A stopped search keeps the groups it confirmed. It measures a walk of a
   drive with and without listing the places phase 1 passes over, and where leaving them unlisted
   saves real time, the walk is told what to pass over rather than passing over it afterwards.

Proves: a file that becomes online-only after the scan, by either recall attribute, is never opened
for its content (a content seam that fails the test if it is); a file that changes mid-read is left
out; a locked, refused or vanished file is left out; equal first and last blocks with different
middles do not match; each algorithm agrees with a published test vector; two volumes on one
spinning disk share one reader; cancellation stops every stage and keeps what was confirmed; the
path reaching Win32 is in its `\\?\` form. Measured and recorded here: a walk of a drive with and
without listing the places phase 1 passes over, and the decision it supports.

### Phase 4 — Marking (Core)

1. **What Storage deletes.** A way to ask every provider where its clean deletes files, without
   planning. Corrected here: not the tool roots, which are refusals (see "Provider targets"), but a
   declaration every provider makes (`CleanedPlacesAsync`), as generous as it needs to be, asking
   a tool where its cache is as discovery already does, and checked against each provider's plan in
   its tests. A provider that only releases a cloud file's local copy names none, because the file
   stays.
2. **What can be kept.** The §7.4 definition, decided in one place: unmarked, on this device and not
   online-only, not refused, not in `%TEMP%` and not under a place from step 1, and on an internal
   drive outside a cloud folder unless its location is a reference. Corrected here: a drive is
   internal only where its `StorageMedia` is `Nvme`, `SolidState` or `Rotational`, never by its
   `DriveType`; `Unknown` (which a USB disk whose bus Windows would not report, or a volume over
   disks of different kinds, reads as) and `Virtual` are not counted on, as `Removable` is not. A
   file with several names qualifies where any one of its names does. A cloud folder is a root
   `ICloudFiles.SyncRoots` lists, and where Windows will not list them, no copy counts as outside
   one. Each place is asked at every path it is reachable at and at its final path, because a
   copy's path is the final one.
3. **The policy.** A copy is refused where Explore's policy refuses it, in a program folder, or
   while it is online-only. A program folder is every install location that names a program,
   including one set aside from the places passed over because it holds a chosen location, and they
   are read whether or not the search passes over them. Reference copies and files with several names are never marked. Explore's
   refusal reasons are reworded so they read correctly on either page, and the refusal set is built
   once for both pages (`MachineProtections`).
4. **Marks.** A per-group mark state that refuses any mark leaving the group with no copy that can
   be kept, and the named rules, each of which chooses the copy it keeps only from the copies that
   can be kept, obeys the policy, and never marks a copy in a cloud folder. The space a group could
   free, which sorts the groups, comes from the same state. While a reference location went
   unsearched, for whatever reason, no rule marks any copy: phase 1 passes over the place it names
   by matching text, and a link on the way to it, or a share that names this computer's own disk,
   can hide it from that match, so a reference copy can still reach a group in the search role.
5. **The confirmation's words.** Built in Core: every copy that goes, the counts, groups and space,
   the sentence for a copy in a cloud folder, and the sentence for a removal larger than a drive's
   Recycle Bin has room for, through a new seam over the bin's size and limit, measured here.

Proves, each by a test that fails without the rule: the last copy that can be kept can never be
marked, by hand, by any rule, or by a stale mark state; where no copy can be kept, every copy stays
unmarked with the reason; a copy in `%TEMP%` or under a root Storage deletes never counts as kept,
and a reference does not change that; a copy on a removable drive, on a USB disk that Windows
reports as fixed, or in a cloud folder counts as kept only in a reference location; a file with one
name in `%TEMP%` and another in Documents can be kept; "keep the newest" keeps the newest copy that
can be kept and marks a newer one on a USB drive; a copy whose cloud file Storage only releases
still counts; a
reference copy and a multi-name file are never marked and can be kept; an online-only copy and a
copy in a program folder are refused; every Explore refusal applies; each named rule marks what it
says and never a copy in a cloud folder; no rule marks a copy while a reference location went
unsearched; nothing is marked when a search finishes; the space figure
counts a reference, refused or multi-name copy as nothing; no copy counts as outside a cloud folder
when the sync roots cannot be read; the confirmation lists every copy, and names the bin's room
where the copies exceed it.

### Phase 5 — Removal (Core)

1. **Shared pieces.** The §5.6 sibling check moves out of `ExploreRemover` into a page-neutral
   type both removers use (`SiblingCheck`), which compares exact names, never names compared without
   regard to case, and the Recycle Bin seam both call says where it put an item
   (`RecycleOutcome.Binned`). Corrected here: Explore's per-item removal is not shared, because
   nothing in it applies to a duplicate. It looks inside a folder for a mail store and deletes by path
   (`FileRemover`, `DirectoryRemover`), while a duplicate is a file deleted through the handle it was
   compared through.
2. **The duplicate remover** (`DuplicateRemover`). It removes only copies the confirmation listed,
   the way the confirmation says (`RemovalConfirmation.Mode`), and only those whose marks still
   stand under the policy and the keeping rule decided again as the removal begins
   (`DuplicateMarks.RejudgeAsync`); a listed copy whose mark no longer stands stays, with why.
   Corrected here: decided once as the removal begins rather than once a copy, as Explore's remover
   does, because the judgement reads the registry and every provider and its inputs are the same
   for every copy of one run. Per copy, immediately before it goes: a copy the group keeps held open
   refusing write, rename and delete until the group's removals have finished; the copy to remove
   held open refusing write; both identified again with the same file ID, size, time and attributes,
   neither online-only (`HeldCopy`); then the same bytes and the same named streams apart from
   `Zone.Identifier`, each file's streams listed through the handle held on it, never by its path
   (`CopyComparison`). Corrected here: both are held by their paths, never by their numbers (see
   "Holding a file open" and "Deleting through a handle"); the path is described first through an
   attributes-only handle that is held as long as the copy is, so no folder above can be renamed,
   and the final path of that handle must be the copy's path, so no link is on the way. The two are
   different files by construction: the copy kept is chosen only among files whose identity no copy
   going shares. Then the removal, files only: permanently through the compared handle, or to the
   Recycle Bin with the item the bin received checked by its file ID. A mismatch, or an item that
   cannot be identified, stops the run at that copy and names what went to the bin. The shell was
   measured to hand back the item it binned, so the Recycle Bin route lands.
3. **§5.6.** Every kept and reference copy still there by its file ID with the same size and time it
   had as the removal began (`CopySurvival`), and every sibling of a removed copy present by its
   exact name.
4. **Running actions.** A `RunningAction` member for the page.

Proves: a copy that changed after the search is not removed; a copy whose bytes differ from the kept
copy is not removed even where its checksum matched (a test with a forced collision); a copy with a
named stream the kept copy lacks is not removed, and one differing only in `Zone.Identifier` is; a
kept copy that another process tries to delete during the removal survives; a file put at the
removed copy's path after the comparison is not what a permanent removal deletes; the same file
reached by two paths is never removed against itself; in a case-sensitive folder, removing `a.txt`
and losing `A.txt` fails §5.6; a removal the bin refuses is never deleted outright; a bin item
whose file ID is not the compared copy's stops the run and is named; the permanent
route keeps every refusal; the path reaching Win32 is in its `\\?\` form. Added here: a copy the
confirmation did not list never goes, and a listed copy whose mark no longer stands stays with its
reason; a reference copy lost during the removal fails §5.6; a bin that does not say where it put a
copy stops the run; a copy whose attributes changed, before or during the comparison, is not
removed; a copy that is only the start of the copy kept is not removed; a copy partly locked by
another program stays and the run still reports; the bin is handed the path in its display form.

### Phase 6 — The page: searching (App)

The navigation item and its spec comment, `DuplicatesPage` and its view-model, built as the other
pages are. The location list with a drive and folder picker and the reference switch; the criteria,
the algorithm and the filters, stored as an `AppPreferences` group; the note of skipped places,
set-aside install locations and unsearchable volumes; the search with stage progress and cancel; the
group list updated in place, never cleared and refilled, because a rebuilt list loses the reader's
place; groups sorted by the space each could free; the sentence on a name or size group that its
files may differ; the space stated as what the copies occupy, which a removal may free less than;
and, where two paths differ, the
difference shown. A `DuplicatesRequest` in `ElevationRequest` carries the locations and their roles
across an elevated reopen, for the file-table route. Legible with no backdrop (§6.5), in light, dark
and high contrast.

Corrected here: groups cannot be sorted as they stream in while the order is decided only at the
end, so the keeping rule is read once the candidates are found and each group is placed as it
arrives (see "Ordering the groups"). The list is cleared once, when a new search starts, because it
is then about something else; within a search each group is one insertion. The notes, the path
difference and whether a picked location can join the list are decisions, so Core makes them
(`DuplicateSearchNotes`, `PathDifference`, `LocationChoice`).

Proves: each control hands Core the value it shows (a test per control); the preferences round-trip
and a file from before them loads; the list keeps the reader's place while groups stream in; a name
or size group shows its sentence and a content group does not; the elevation request round-trips
every location and role; the page searches a scratch folder of known duplicates when driven with
the `verify` skill.

### Phase 7 — The page: marking and removing (App)

Marking by hand and by rule, the reason for every refusal reachable by pointer, keyboard and screen
reader, the confirmation dialog with its list, and the result after a removal, including what each
refused copy's check said.

Two hazards phase 6 leaves to this phase. A location's role changed after a search leaves the groups
and marks shown in the old role, so a role change clears the results or marks them stale, and no
rule or removal runs on them until a search runs again. `DuplicateMarks.Add` runs on the page's
thread while `DuplicateMarks.RejudgeAsync`, `RemovalConfirmation` and `DuplicateRemover` read the
groups off it, so no confirmation or removal starts while a search runs.

Corrected here: both hazards are wider than a role and a removal. A location added or taken away
after the search leaves its groups in roles the user no longer chose as surely as a role change does
(a folder added as a reference inside a searched one), so any change to the locations or their roles
makes the results stale (`DuplicateSearch.WhyResultsDoNotApply`), and they apply again only when the
locations match the search's, in any order. A rule reads the groups off the page's thread too
(`DuplicateMarks.Run` opens a folder rule's folder), and so does the judgement every confirmation and
removal begins with, so Core refuses both until the page says the search has ended
(`DuplicateMarks.Complete`), and refuses a group added after it. The marks themselves
(`GroupMarks`) and the refusals' cache (`CopyRefusals`) are not safe to change or ask on two threads,
so no mark changes, by hand or by rule, while a rule, a confirmation or a removal runs, and no search
starts under one, and the page holds its locations meanwhile. Because a page's controls are not
the only way to change them, a removal also asks the page again, once the confirmation is built and
once the user has answered, whether its marks still apply, and asks or removes nothing where they
do not (`DuplicateActions.RemoveAsync`). After a
removal has begun the groups describe the disk as it was, so Core refuses every later rule,
confirmation and removal on those marks (`DuplicateMarks.WasRemovedFrom`) until a search runs again.
A mark by hand stays open while the search runs, because it is made on the page's thread, as each
group is added.

Proves: a rule's marks are what Core decided; the page offers no way past a Core refusal; the dialog
shows Core's words; a role changed after a search stops every rule and removal until the next
search; no confirmation or removal can start while a search runs; a removal driven with the `verify` skill moves the marked copies to the Recycle
Bin and leaves every kept copy.

### Phase 8 — The checksum cache and export

1. **The cache.** Checksums kept under the volume serial, file ID, size, last-modified and change
   times and the algorithm, in a store under `%LOCALAPPDATA%\Deguffer` reached through
   `IUserEnvironment`, which holds no path. Not kept for FAT or exFAT volumes. A corrupt store loads
   empty.
2. **Export.** CSV, one row per copy with its group, path, size, times, algorithm and checksum,
   quoted so a path holding a comma or a quote reads back as itself, written where the page's save
   dialog says.

Corrected here: the change time was not read by anything, so the file description gained it (see
"Change time"). Whether a volume's numbers stay with its files is decided by the name it gives its
file system, NTFS or ReFS, and its files' identification by `FileIdInfo`, rather than by naming
FAT and exFAT, so a file system Deguffer does not know, or a volume that will not name one, keeps
nothing either (`ChecksumCache.Keeps`). Both stages' values are kept, the first and last blocks as
well as the whole, so a second search over an unchanged tree reads no file's content. A value stands in
for reading the bytes, never for opening them: it is used only once the content is open and judged
as a read would judge it, so a file another program holds or this account may not read is left out
as before, and it is kept only from a read whose change time did not move while it ran. The store is bounded: a value unused for 180 days goes, and
past 500,000 the values used longest ago go first. The page's save dialog is the Windows App SDK
`FileSavePicker`, for the reason the folder picker is (`CsvFileDialog`), and the file is written
beside the one chosen under a name of its own and moved over it once whole.

Proves: a second search over an unchanged tree reads no file in full; a change to any key field
misses the cache; the store holds no path (a test reads it); a FAT volume is never cached; a corrupt
store loads empty; the CSV round-trips hostile paths and a long path whole. Added here: a file
rewritten with its old last-modified time put back is read again; a file that went online-only is
not answered from the cache, and nor is one another program now holds; a value read while the file's change time moved is not kept; a value
unused too long, or past the bound, goes; the page saves only once a search with results has
ended, in the order the groups are shown.

### Phase 9 — Verification and close

Drive the whole feature with the `verify` skill, unelevated and elevated, over a scratch tree that
holds hard links, a junction loop, a substituted drive, a case-sensitive folder, a named stream, a
long path, an empty file, a locked file, a reference folder, a file the bin cannot take and, where
the machine has one, a OneDrive online-only file, and a volume mounted in a folder with no drive
letter, holding a program's install location and a place a Storage clean names, so Windows gives
their final paths in the `\\?\Volume{GUID}\` form that `ResolvedPlaces.FollowedTo` names by the
mount (no test can produce that form without mounting such a volume). Attach a scratch disk with a FAT32 and an exFAT
volume, measure whether each answers `FileIdInfo` or only the older call, and whether a file keeps
its ID across a rename and a move, record it under the technical facts, and search both, seeing
that the searches leave the checksum store as it was. Read a
Recycle Bin's size from the x86 build, which asks for it with the packed 20-byte `SHQUERYRBINFO`
that only a 32-bit process can confirm, and record it there too. Measure a
search of a real drive and record the figures here, redacted. Update `README.md`. Flip this banner to complete, move this file to `done/`
with its links corrected (`_spec.md` becomes `../_spec.md`, and §7.4's link becomes
`done/duplicates.md`), and close #297.

## What every phase passes before the next starts

1. `dotnet build Deguffer.sln` and `dotnet test Deguffer.sln` pass, read rather than assumed.
2. Every behaviour test has been seen to fail with the code it guards mutated, because a test written
   after the code and green on its first run has proved nothing.
3. The phase is checked for data loss against §7.4 and against the engineering gates, and nothing
   found is left unfixed.
4. The phase lands on `main`, and the status log below says what landed and what it decided.

## Status log

- 2026-10-08: phase 0 landed. §2 amended (the duplicate-finder non-goal replaced by a goal and a
  similarity non-goal) and §7.4 written; this plan written from the research above.
- 2026-10-09: phase 1 landed. The search as values (`DuplicateSearch`), refusing a size range or
  an extension list that admits no file; locations resolved to the final path of an opened handle
  (`SearchLocations`, `FileInformation`), a share, a network drive and a cloud drive refused before
  anything is opened on them; the places Explore refuses at and below
  (`ExploreActionPolicy.RefusedAtAndBelow`, `TopOfVolumeRefusals`, `RefusalWatch`) and program
  folders (`ProgramFolders`) passed over and named; one file-table read per volume
  (`ExploreScanner.ScanFoldersAsync`), each folder found in it by its exact name and its route
  noted; a hidden and system column on the scan tree; folders Windows would not list, and
  locations read from a table not read whole, named as unread; and the files kept and grouped by
  length and name (`CandidateFinder`). Decided: the attributes column rather than a later read; a
  mail store, and a folder named like one, are passed over; one location holds another only on
  the same volume, because the enumeration of a drive never crosses a volume mounted in one of its
  folders; a folder matching a location only when case is ignored takes the reference role where
  either role is one; the place a reference that went unsearched names is passed over, even where
  the user searches the places passed over by default, and no rule may mark while one went
  unsearched (§7.4, phase 4), because a link or a share naming this computer's own disk can hide
  its copies from that match. Corrected here: candidates group by length only where the size or
  the content is a criterion, not always; `CloudFiles.Resolve` already followed every link, so the
  final-path call moved from it rather than being new.
- 2026-10-09: phase 2 landed. One file-information seam (`FileInformation`): the attributes-only
  handle, identity, length, names, time, attributes and reparse tag, answering identified, gone or
  unreadable (`FileInformation.Describe`), with the cloud files, the occupancy probe and the
  hard-link scanner moved onto it. The files that share a group by what the tree holds are
  identified (`CandidateIdentification`) and grouped again by what Windows says now
  (`CandidateGrouping.ByTheFiles`): one file however many paths reached it, with every name where it
  has several, a reference where any path reaching it is one, and times compared to the tick. A
  file gone, refused, empty or a link by the time it is opened is left out and counted, a refusal
  apart from a file that is gone. Decided: the route that identifies a volume's files is chosen
  once a volume, because the older call gives the serial number at half the width, and a volume
  neither call identifies is not searched, with each location on it named, a reference included.
  Corrected here: named streams are listed in phase 5 through the held handle, not at search time
  by path; the FAT and exFAT measurement moves to phase 9, which runs elevated.
- 2026-10-09: phase 3 landed. Checksums behind one running `Checksum` over System.IO.Hashing and
  `IncrementalHash`, each pinned to a published vector, SHA3-256 offered only where Windows has it
  (`ChecksumAlgorithms`); a content seam (`ContentReader`) that describes the path through the
  attributes-only handle, leaves an online-only file out unopened, opens the content by its file
  ID through that held handle (`FileInformation.Hold`, `FileInformation.OpenById`) sharing only
  reading, and describes it again through the content handle before the first byte and after the
  last, counting a failed read and a changed file apart from a gone one (`LeftOutFiles`); staged
  matching (`ContentMatching`) of the first and last 64 KiB, then the whole of larger files, read
  in lanes by physical disk (`ReadingLanes`: one reader unless solid state, four there), each file
  carrying the volume its location was resolved to; and the searcher (`DuplicateSearcher`), which
  streams each `DuplicateGroup` and reports progress by stage. Decided: content is opened by file
  ID, because a path open follows any link put on the path after the description and can reach a
  share or recall a cloud file before a check could stop it; where Windows will not open a file by
  its ID, the path is opened only on a volume that answered that it holds no reparse points and is
  reached by its own drive letter (`LocalVolume.CannotHoldLinks`), a volume that would not answer
  never qualifying, and otherwise the read fails; a stop while reading content answers what was
  confirmed, marked stopped, even where that is nothing, and a stop before the candidates were all
  found throws; a group whose last read finishes after the stop is not confirmed. Measured: a warm
  walk of a system drive took a median 6,578 ms, of which the places passed over by default took
  2,701 ms (41%, and 661,852 of 2.49 M entries), so the walk is now told what to pass over
  (`PassedOverBelow`, asked by both the walk and the candidate walk) and leaves it unlisted; the
  file table, read whole anyway, ignores it. Corrected here: CRC-32's byte order, the throughput
  figures, the placeholder mode, and how the content is opened (by file ID, not `File.OpenHandle`
  on the path), in the technical facts above.
- 2026-10-09: phase 4 landed. Every provider declares where its clean deletes, without planning
  (`ICleanupProvider.CleanedPlacesAsync`, `CleanedPlace`), each checked against its own plan in its
  tests (`CleanedPlaceCoverage`); the copies that can be kept (`CopyKeeping`), the copies never marked
  and refused (`CopyRefusals`), a mark state per group that always keeps a copy and judges its marks
  again at each question (`GroupMarks`), the named rules (`MarkingRule`, `DuplicateMarks`), and the
  confirmation's words (`RemovalConfirmation`) over a seam on each bin's size and limit
  (`RecycleBinRooms`); each copy's size on disk, from the handle that identifies it; and Explore's
  refusal set and Storage's places asked of one set of providers for both pages
  (`MachineProtections`), with Explore's refusal reasons reworded to read on either page. Decided:
  the tool roots are refusals, not where a clean deletes, so every provider declares its places,
  generously, the default and every configured or reported location; a drive is internal only where
  its disks are NVMe, solid state or rotational; an install location set aside because it holds a
  chosen location still refuses the copies in it (§7.4 amended); the search names every reference
  location it did not search (`CandidateFinding.UnsearchedReferences`): one not resolved, one on a
  volume whose files cannot be identified, and one at or inside a place passed over or not read,
  but not one that was searched and holds such a place, since every whole drive holds one; a
  reference named so stops every rule while marking by hand stays open; a rule adds to the marks there are, and a keep
  rule breaks a tie by the shorter path, then the path's text; the space shown is what the copies
  occupy, as Windows reports it, which a removal may free less than, since shared clusters count in
  full and the Recycle Bin frees nothing until it is emptied (§7.4 reworded); the confirmation
  judges the marks again against the machine as it is then (`DuplicateMarks.RejudgeAsync`,
  `RemovalConfirmation.ForAsync`), keeping every mark: Explore's policy, Storage's places, the
  installed programs (`MachineProtections.ProgramFolders`, keeping every program folder the search
  knew), the temporary folder, the cloud folders, and each drive's bus asked again rather than
  remembered (`VolumeMediaCache.Now`), because a disk moved into a USB dock keeps its volume and
  every file ID; a program folder, like a place Storage cleans, is asked as named and at the final
  path a link on the way leads to, and an entry that leads to a folder naming no program is set
  aside; neither is opened on a share, a network drive or a cloud drive to follow it
  (`ResolvedPlaces.FollowedTo`). Measured: what a bin holds and its limit, in the technical facts.
  Corrected here: step 1 (not the tool roots), the internal-drive test, and the facts on the
  Recycle Bin and on provider targets.
- 2026-10-09: phase 5 landed. The duplicate remover (`DuplicateRemover`): the copies the
  confirmation listed whose marks still stand under the judgement made as the removal begins, each
  held by its path (`HeldCopy`) beside a copy its group keeps, held refusing write, rename and
  delete, both found unchanged since the search, and compared byte for byte with their named
  streams apart from `Zone.Identifier` (`CopyComparison`, `NamedStreams`); a permanent removal
  through the compared handle (`HandleDeletion`), and a Recycle Bin removal whose item is identified
  by its file ID (`RecycleOutcome.Binned`, `BinnedItem`), the run stopping where it is not the file
  compared or cannot be told; a result per copy naming the check it failed (`RemovalCheck`,
  `DuplicateRemovalReport`); §5.6 by file ID for every kept and reference copy (`CopySurvival`) and
  by exact name for every sibling (`SiblingCheck`, now Explore's too); and a running action for the
  page. Decided: the removal takes its mode and its list from the confirmation, so a mark the
  confirmation dropped never goes even where it stands again by the removal; the policy and the
  keeping rule are decided once as the removal begins; the copy kept is chosen by identity, never by
  path; the lengths are compared before the bytes, because a group matched by name or time alone
  can hold a copy that is only the start of the copy kept; and a read another program's lock
  refuses leaves that copy where it is, with its reason, never ending the run unreported. Measured: the shell's progress sink names the item it binned, with its file ID unchanged; a
  handle opened by number refuses a write but not a rename or a delete, and cannot delete its file;
  a named stream opens relative to a held handle; the stream listing answers through an
  attributes-only handle. Corrected here: step 1 (what is shared), step 2 (both copies held by
  path, decided as the removal begins), and the facts on deleting through a handle, holding a file
  open, the Recycle Bin and Explore's removal.
- 2026-10-09: phase 6 landed. The Duplicates destination (`DuplicatesPage`): a location list with
  a drive picker, a folder picker and a reference switch per location
  (`DuplicateLocationsViewModel`, `LocationChoice`); the criteria, checksum and filters stored as a
  preferences group (`AppPreferences.Duplicates`, `DuplicatePreferences`, `SizeLimit`,
  `ExtensionFilter.Parse`), each handed to the search as the control shows it; the search with its
  stage, progress and stop; the notes on every place not searched, passed over or not read, every
  install location set aside and every file left out (`DuplicateSearchNotes`); the groups, each
  placed as it is confirmed by the space it could free and never moved, with the space stated as
  what the copies occupy, the may-differ sentence on a group not matched on content
  (`DuplicateGroup.MayDiffer`), the checksum by the name other tools print it under
  (`ChecksumAlgorithms.Name`), and where each copy's path differs from the others' picked out by
  weight and underline as well as colour (`PathDifference`); and the elevated reopen
  (`DuplicatesRequest`), which searches the same locations by the file table. Decided: the searcher
  hands over the candidates before any content is read and the marks are made then
  (`DuplicateSearchRun`), so the keeping rule a group is placed by is the one marking reads, and no
  row moves; a space separates typed extensions, as a comma or a semicolon does; a size keeps its
  amount and unit as typed, and a unit chosen with no amount stays chosen; a checksum the machine
  does not offer searches with XXH128; the same path is not listed twice, compared case and all; a
  drive Explore refuses is refused for the same reason; the drive picker's templates are one
  dictionary both pages use (`DriveTemplates`). Verified by driving the page over a scratch tree of
  known duplicates: both groups found, the one-letter folder difference picked out, and a name-only
  search showing the sentence; an ordinary launch given the elevated reopen's arguments opened on
  the page and searched them. With the backdrop off the page is legible in dark; in light the rail
  and title bar draw black on every page, a defect older than this phase (#299); high contrast was
  not switched on, since that changes the whole desktop, and the page uses only theme brushes and
  marks a path difference by weight and underline as well as colour. Corrected here: how the
  groups are ordered as they stream (the technical facts, "Ordering the groups"), and what phase 6
  decides in Core.
- 2026-10-09: phase 7 landed. Marking by hand, each check box asking Core and showing its refusal
  on the row, with why each copy may not be marked or kept listed beneath it
  (`CopyKeeping.WhyNotMarked`, `CopyKeeping.Standing`); the named rules, run off the page's thread
  and stoppable between groups, each group a rule left alone saying why (`RuleOutcome.Summary`);
  the confirmation dialog with Core's title, summary and warnings and every copy that goes
  (`RemovalConfirmation.Title`, `ConfirmLabel`), to the Recycle Bin by default and permanently only
  from its own button; and the removal and what became of each copy, through one Core flow
  (`DuplicateActions`, `IDuplicateConfirmationPrompt`) that asks nothing where no mark stands and
  records the removal as running until it reports. Both hazards phase 6 left are closed, wider than
  they were stated (see phase 7, "Corrected here"): any change to the locations or their roles makes
  the results stale (`DuplicateSearch.WhyResultsDoNotApply`), and Core refuses a rule, a judgement,
  a confirmation or a removal until the search has said its last group is in
  (`DuplicateMarks.Complete`), and again once a removal has begun (`DuplicateMarks.WasRemovedFrom`).
  Decided: a location change of any kind, not only a role, makes the results stale, and putting the
  locations back as they were makes them apply again; a mark by hand stays open while the search
  runs, because it is made on the page's thread; nothing changes the marks, the locations or the
  search while a rule, a confirmation or a removal runs, and a removal asks the page again, once the
  confirmation is built and once the user has answered, whether its marks still apply; one removal
  per search, since the groups then describe the disk as it was. Verified by driving the page over a
  scratch tree of known duplicates: a mark Core refused showed unchecked with its reason; "keep the
  copy with the shortest path" marked the two longer copies; the dialog listed every copy with
  Cancel as its default; confirming moved the marked copies to the Recycle Bin, from their own
  folders, and left every kept copy; a role changed after the search closed every mark, rule and
  removal, and the locations were held while the dialog was open. The page was not checked with the backdrop off or in
  high contrast this phase; its new text uses theme brushes and plain text only.

## Limits that stay open

- **A name search on the file-table route.** The table keeps one name a record, so a file with
  several names is matched by name only under the name the table kept, while the walk matches it
  under each name it lists. The file is still found with every name; only a match under its other
  names is missed, which leaves a copy unshown and never removes one. Closing it needs the table's
  reader to keep every name.

- **Whole duplicate folders.** Shown as their files. Matching folders as units needs a rule for
  folders that only look alike (a program's folder, a project), and §7.4 does not authorise
  removing a folder.
- **Replacing a copy by a hard link.** Not authorised (§7.4), for the backup and save-by-rename
  reasons it gives. Re-opening it needs an answer to both.
- **Space shared by block cloning.** On ReFS and a Dev Drive a removal can free less than the
  figure shown, until Windows reports what a cloned file shares.
- **A program that registers no install location.** A portable program, or a game library a
  launcher keeps without an entry, is not recognised as a program folder. The confirmation's list
  is where the user sees it.
- **A cloud folder Windows does not list.** A sync client that does not register with the Cloud
  Files API, or a root Windows will not hand back, is not known to be a cloud folder, so a copy there
  can count as kept and is not marked by hand only. The confirmation's list is where the user sees it.
- **A place a clean deletes that its provider cannot name.** A provider names the places it can
  find without planning, and a few it cannot: a Delivery Optimization cache a policy moved to another
  drive, a pnpm `dlx` cache moved by its `cache-dir` setting, a Maven repository named by
  `-Dmaven.repo.local` or the global `settings.xml`, an app Squirrel installed after the places were
  asked, and whatever Windows' own cleanups reach beyond the folders their provider declares. A
  copy in one can count as kept. The confirmation's list is where the user sees it.
- **A stream added to a copy at the last moment.** Holding a file refusing write does not stop
  another program adding a named stream beside its content. The copy's streams are listed again
  once it is compared and must be the streams compared, which leaves only the moment between that
  listing and the removal itself.
- **Times set back on purpose.** A program that rewrites a file and then sets both its
  last-modified and change times back, at the same length, leaves its cached checksum standing, so
  the file can be grouped by its old content until it changes again. It is never removed on that,
  because a removal compares the bytes.
- **A range locked after a checksum was kept.** A program that locks part of a file, rather than
  holding the whole of it, refuses only a read of that part, so a search that uses the file's kept
  checksum does not see the lock, where a read would have left the file out. The file is grouped,
  and a removal still reads it.
- **A catalogue that names a file.** Lightroom, a music library or a project file can name the copy
  a user removes. Deguffer cannot see that; the confirmation lists every copy so the user can.
