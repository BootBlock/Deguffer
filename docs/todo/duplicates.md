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
  `Crc32` and `Crc64` behind `NonCryptographicHashAlgorithm`. It is the first package
  `Deguffer.Core` references, and the only one this plan adds. The App ships untrimmed (see
  [the AOT evaluation](aot-and-single-file-evaluation.md)), so trimming does not apply. BLAKE3 is
  not offered, because its maintained .NET port would be a second dependency for the role XXH128
  already fills.
- **Cryptographic checksums come from Windows (CNG)** through `System.Security.Cryptography`. MD5,
  SHA-1 and the SHA-2 family work on every supported Windows; SHA3-256 needs Windows 11 build 25324
  or later, so it is offered only where `SHA3_256.IsSupported`.
- **Throughput.** Native XXH3 runs near 60 GB/s and MD5 near 0.6 GB/s on an i7-9700K; .NET 10's
  SHA-256 measured about 2.8 GB/s on one Ryzen 9 9950X thread. The managed `XxHash128` has no
  published figure **(unverified)**. fclones' author reports that the choice of checksum barely
  matters except on a fast SSD or cached data, because the disk sets the pace, and that several
  readers on one spinning disk are much slower than one.
- **Reading.** `File.OpenHandle` and `RandomAccess.Read(handle, span, offset)` read the first and
  last blocks and the full content without a shared position.
- **Identity.** `GetFileInformationByHandleEx(FileIdInfo)` returns the volume serial and the 128-bit
  file ID, which ReFS needs because its 64-bit index is not unique. A handle opened for
  `FILE_READ_ATTRIBUTES` with `FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS` reads it
  without following a link or recalling a cloud file. `FindFirstFileNameW` lists every name of a
  file, and `FindFirstStreamW` every named stream. On FAT and exFAT a file ID can change after a
  defragmentation or a rename, so it identifies a file for one search and never for the cache.
  Whether `FileIdInfo` answers on FAT and exFAT at all is **(unverified)**.
- **Cloud files.** Reading the data of a file marked `RECALL_ON_DATA_ACCESS` downloads it, and no
  documented call lets a process read it without that. `RtlSetProcessPlaceholderCompatibilityMode`
  changes how a placeholder looks, not whether it downloads, and two Microsoft pages disagree about
  the default an unpackaged process sees **(unverified)**; `CloudFiles` already sets the thread's
  mode where it needs to see placeholders. Opening a `RECALL_ON_OPEN` file for its content can
  download it before any check on that handle runs, so the check uses an attributes-only handle
  first. A fully downloaded OneDrive file keeps
  its cloud reparse tag, so the tag is no sign of a file being online-only; the attributes are.
  Deleting an online-only OneDrive file deletes it everywhere; the OneDrive recycle bin keeps it for
  30 days (personal) or 93 (work or school).
- **Space.** ReFS block cloning, which Windows 11 24H2 uses for ordinary copies on a Dev Drive, and
  Windows Server deduplication (`IO_REPARSE_TAG_DEDUP`) let copies share clusters. No user-mode API
  reports what a cloned file shares **(unverified)**, so the freed figure is a lower bound there.
- **Recycle Bin.** When a bin is full, Windows deletes its oldest items outright to make room for
  new ones. How Deguffer reads a bin's room on a volume (its size from `SHQueryRecycleBin`, its
  limit from the bin's settings) is **(unverified)**, and nothing in Core asks it yet. Whether
  `IFileOperation`'s progress sink hands back the item it put in the bin, so that its file ID can be
  read, is **(unverified)**.
- **Deleting through a handle.** `SetFileInformationByHandle` with `FileDispositionInfoEx` deletes
  the file a handle holds, whatever its path names by then.

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
  decides whether a drive is internal.
- **Cloud folders.** `ICloudFiles.SyncRoots` lists the roots registered with the Cloud Files API and
  returns null where Windows will not list them. It leaves out a root Windows will not hand back,
  such as one under `AppData\Local`, and a client that does not register at all.
- **Provider targets.** Nothing yet answers "what would Storage delete" without planning: the
  planner offers only a full plan, which runs subprocesses and depends on the age setting.
  `ICleanupProvider.ToolRoots` and `DiscoverToolRootsAsync` declare the roots Explore protects: 42
  of the 68 providers built on `CleanupProviderBase` declare a root, and 57 counting discovered
  ones.
- **Paths.** `LongPath.Canonical` normalises a path's form and expands 8.3 names; it resolves no
  link and no drive letter. `ReachedFolder.At` gives every place a folder is reachable at, through
  substituted drives and volumes mounted in folders. `FileInformation.FinalPath` (phase 1) follows
  every link on the way to a path, its own name included, and a substituted letter, to the path an
  opened handle gives, and is the one declaration of `GetFinalPathNameByHandle`: it moved there from
  `CloudFilesNative`, whose `CloudFiles.Resolve` already followed every link and now calls it.
- **File information.** `HardLinkAwareScanner` (`FileStandardInfo`) and `CloudFilesNative`
  (`FileBasicInfo`, `FileAttributeTagInfo`) each declare their own `GetFileInformationByHandleEx`.
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
  removes each item with no hook between items, and its §5.6 check compares sibling paths ignoring
  case. `ShellRecycleBin` uses `IFileOperation` with `FOFX_RECYCLEONDELETE`, so a file the bin
  cannot take fails rather than going outright, and it refuses a path the shell cannot parse.
- **Elevation.** `ElevationRequest` has `Preview`, `InstalledApps` and `ExploreRequest`, each a
  switch on the command line with a round-trip test.

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
2. **Names and streams.** Every name of a file through `FindFirstFileNameW`, and its named streams
   through `FindFirstStreamW`, on both routes.
3. **One file is one file.** Candidates with one identity become one file with all its names, never a
   group, which is the end-to-end proof that a file is searched once however it was reached. A file
   with several names is never marked and never counted, and it can still be the copy a group keeps.
4. **Full-precision times.** The modified-time criterion and every later check use the identity's
   times, never the tree's.
5. **FAT and exFAT.** Measured: whether `FileIdInfo` answers there, and what the older call gives
   if it does not. A volume where no identity can be had is not searched, and the search says so.

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
   content through `File.OpenHandle` and `RandomAccess`. The default placeholder mode an unpackaged
   Deguffer sees is measured here.
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

1. **What Storage deletes.** A way to ask every provider which roots its clean deletes files under,
   without planning: the declared and discovered tool roots of each provider whose clean deletes,
   with a declaration added to each such provider that has none. A provider that only releases a
   cloud file's local copy names none, because the file stays.
2. **What can be kept.** The §7.4 definition, decided in one place: unmarked, on this device and not
   online-only, not refused, not in `%TEMP%` and not under a root from step 1, and on an internal
   drive outside a cloud folder unless its location is a reference. A drive is internal where its
   `StorageMedia` is not `Removable`, never by its `DriveType`. A file with several names qualifies
   where any one of its names does. A cloud folder is a root
   `ICloudFiles.SyncRoots` lists, and where Windows will not list them, no copy counts as outside
   one.
3. **The policy.** A copy is refused where Explore's policy refuses it, in a program folder, or
   while it is online-only. Reference copies and files with several names are never marked. Explore's
   refusal reasons are reworded so they read correctly on either page, and the refusal set is built
   once for both pages.
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

1. **Shared pieces.** The per-item removal and the §5.6 sibling check move out of `ExploreRemover`
   into page-neutral types that both removers use, and the sibling check compares exact names,
   never names compared without regard to case.
2. **The duplicate remover.** Per copy, immediately before it goes: the policy and the keeping rule
   decided again; the kept copy opened and held open, refusing write and delete, until the removal
   has finished; the copy to remove held open refusing write; the two proved to be different files;
   both identified again with the same file ID, size, time and attributes, neither online-only; then
   the same bytes and the same named streams apart from `Zone.Identifier`. Then the removal, files
   only: permanently through the compared handle (`FileDispositionInfoEx`), or to the Recycle Bin
   with the item the bin received checked by its file ID. A mismatch, or an item that cannot be
   identified, stops the run at that copy and names what went to the bin. If the shell turns out not
   to hand back the item it binned, this phase records it here and takes the question back to §7.4
   before the Recycle Bin route lands, rather than shipping a check that cannot be made.
3. **§5.6.** Every kept and reference copy still there by its file ID with the same size and time,
   and every sibling of a removed copy present by its exact name.
4. **Running actions.** A `RunningAction` member for the page.

Proves: a copy that changed after the search is not removed; a copy whose bytes differ from the kept
copy is not removed even where its checksum matched (a test with a forced collision); a copy with a
named stream the kept copy lacks is not removed, and one differing only in `Zone.Identifier` is; a
kept copy that another process tries to delete during the removal survives; a file put at the
removed copy's path after the comparison is not what a permanent removal deletes; the same file
reached by two paths is never removed against itself; in a case-sensitive folder, removing `a.txt`
and losing `A.txt` fails §5.6; a removal the bin refuses is never deleted outright; a bin item
whose file ID is not the compared copy's stops the run and is named; the permanent
route keeps every refusal; the path reaching Win32 is in its `\\?\` form.

### Phase 6 — The page: searching (App)

The navigation item and its spec comment, `DuplicatesPage` and its view-model, built as the other
pages are. The location list with a drive and folder picker and the reference switch; the criteria,
the algorithm and the filters, stored as an `AppPreferences` group; the note of skipped places,
set-aside install locations and unsearchable volumes; the search with stage progress and cancel; the
group list updated in place, never cleared and refilled, because a rebuilt list loses the reader's
place; groups sorted by the space each could free; the sentence on a name or size group that its
files may differ; the freed figure stated as a lower bound; and, where two paths differ, the
difference shown. A `DuplicatesRequest` in `ElevationRequest` carries the locations and their roles
across an elevated reopen, for the file-table route. Legible with no backdrop (§6.5), in light, dark
and high contrast.

Proves: each control hands Core the value it shows (a test per control); the preferences round-trip
and a file from before them loads; the list keeps the reader's place while groups stream in; a name
or size group shows its sentence and a content group does not; the elevation request round-trips
every location and role; the page searches a scratch folder of known duplicates when driven with
the `verify` skill.

### Phase 7 — The page: marking and removing (App)

Marking by hand and by rule, the reason for every refusal reachable by pointer, keyboard and screen
reader, the confirmation dialog with its list, and the result after a removal, including what each
refused copy's check said.

Proves: a rule's marks are what Core decided; the page offers no way past a Core refusal; the dialog
shows Core's words; a removal driven with the `verify` skill moves the marked copies to the Recycle
Bin and leaves every kept copy.

### Phase 8 — The checksum cache and export

1. **The cache.** Checksums kept under the volume serial, file ID, size, last-modified and change
   times and the algorithm, in a store under `%LOCALAPPDATA%\Deguffer` reached through
   `IUserEnvironment`, which holds no path. Not kept for FAT or exFAT volumes. A corrupt store loads
   empty.
2. **Export.** CSV, one row per copy with its group, path, size, times, algorithm and checksum,
   quoted so a path holding a comma or a quote reads back as itself, written where the page's save
   dialog says.

Proves: a second search over an unchanged tree reads no file in full; a change to any key field
misses the cache; the store holds no path (a test reads it); a FAT volume is never cached; a corrupt
store loads empty; the CSV round-trips hostile paths and a long path whole.

### Phase 9 — Verification and close

Drive the whole feature with the `verify` skill, unelevated and elevated, over a scratch tree that
holds hard links, a junction loop, a substituted drive, a case-sensitive folder, a named stream, a
long path, an empty file, a locked file, a reference folder, a file the bin cannot take and, where
the machine has one, a OneDrive online-only file. Measure a search of a real drive and record the
figures here, redacted. Update `README.md`. Flip this banner to complete, move this file to `done/`
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

## Limits that stay open

- **Whole duplicate folders.** Shown as their files. Matching folders as units needs a rule for
  folders that only look alike (a program's folder, a project), and §7.4 does not authorise
  removing a folder.
- **Replacing a copy by a hard link.** Not authorised (§7.4), for the backup and save-by-rename
  reasons it gives. Re-opening it needs an answer to both.
- **Space shared by block cloning.** The freed figure stays a lower bound on ReFS and a Dev Drive
  until Windows reports what a cloned file shares.
- **A program that registers no install location.** A portable program, or a game library a
  launcher keeps without an entry, is not recognised as a program folder. The confirmation's list
  is where the user sees it.
- **A cloud folder Windows does not list.** A sync client that does not register with the Cloud
  Files API, or a root Windows will not hand back, is not known to be a cloud folder, so a copy there
  can count as kept and is not marked by hand only. The confirmation's list is where the user sees it.
- **A catalogue that names a file.** Lightroom, a music library or a project file can name the copy
  a user removes. Deguffer cannot see that; the confirmation lists every copy so the user can.
