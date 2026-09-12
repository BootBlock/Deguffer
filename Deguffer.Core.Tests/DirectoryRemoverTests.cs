using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

public sealed class DirectoryRemoverTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ReportsSuccessForATreeThatWasAlreadyGone()
    {
        var outcome = await DirectoryRemover.RemoveAsync(Path.Combine(_temp.Path, "never-existed"));

        Assert.True(outcome.RootRemoved);
        Assert.Equal(0, outcome.BytesReclaimed);
    }

    /// <summary>A path already gone was not removed by this run, so nothing is counted for it.</summary>
    [Fact]
    public async Task CountsNothingForATreeThatWasAlreadyGone() =>
        Assert.Equal(0, (await DirectoryRemover.RemoveAsync(Path.Combine(_temp.Path, "never-existed"))).EntriesRemoved);

    [Fact]
    public async Task CountsEveryEntryItRemovedTheFolderIncluded()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(1024, "cache", "a.bin");
        _temp.CreateFile(0, "cache", "deep", "empty.tmp");
        _temp.CreateDirectory("cache", "deep", "nothing");

        var outcome = await DirectoryRemover.RemoveAsync(root);

        // cache, deep and nothing, a.bin and empty.tmp.
        Assert.Equal(5, outcome.EntriesRemoved);
        Assert.False(Directory.Exists(root));
    }

    /// <summary>The removal a leftover of empty folders is offered for: no bytes, one entry.</summary>
    [Fact]
    public async Task AnEmptyFolderRemovedIsOneEntryAndNoBytes()
    {
        var outcome = await DirectoryRemover.RemoveAsync(_temp.CreateDirectory("session"));

        Assert.Equal(1, outcome.EntriesRemoved);
        Assert.Equal(0, outcome.BytesReclaimed);
    }

    /// <summary>What stays is not counted: a file the guard keeps, and every folder holding it.</summary>
    [Fact]
    public async Task CountsNothingTheGuardKeptOrAnyFolderHoldingIt()
    {
        var root = _temp.CreateDirectory("cache");
        TempDirectory.Age(_temp.CreateFile(10, "cache", "old.bin"), TimeSpan.FromDays(30));
        _temp.CreateFile(10, "cache", "live", "recent.bin");
        _temp.CreateDirectory("cache", "gone");

        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.WithinHours(8, DateTime.UtcNow));

        // Removed: old.bin and gone. Stayed: recent.bin, live and cache.
        Assert.Equal(2, outcome.EntriesRemoved);
        Assert.True(Directory.Exists(root));
    }

    /// <summary>A link inside the tree is removed as a link: one entry, and its far side untouched.</summary>
    [Fact]
    public async Task ALinkRemovedIsOneEntryAndItsFarSideIsUntouched()
    {
        var outside = _temp.CreateDirectory("elsewhere");
        var bystander = _temp.CreateFile(10, "elsewhere", "bystander.bin");

        var root = _temp.CreateDirectory("cache");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.Equal(2, outcome.EntriesRemoved);
        Assert.True(File.Exists(bystander), "the removal followed a link");
    }

    [Fact]
    public async Task ARootThatIsALinkIsOneEntryAndItsFarSideIsUntouched()
    {
        var outside = _temp.CreateDirectory("elsewhere");
        var bystander = _temp.CreateFile(10, "elsewhere", "bystander.bin");

        var root = Path.Combine(_temp.Path, "cache");
        Directory.CreateSymbolicLink(root, outside);

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.Equal(1, outcome.EntriesRemoved);
        Assert.True(File.Exists(bystander), "the removal followed a link at its root");
    }

    [Fact]
    public async Task RemovesANestedTreeAndReportsWhatItReclaimed()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(1024, "cache", "a.bin");
        _temp.CreateFile(2048, "cache", "deep", "b.bin");
        _temp.CreateFile(4096, "cache", "deep", "deeper", "c.bin");

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.True(outcome.RootRemoved);
        Assert.True(outcome.Refused.IsEmpty);
        Assert.Equal(1024 + 2048 + 4096, outcome.BytesReclaimed);
        Assert.False(Directory.Exists(root));
        Assert.Empty(outcome.LeftStanding);
        Assert.True(outcome.RefusedFolders.IsEmpty);
    }

    /// <summary>
    /// A folder a program is working in is one Windows will not remove, and removal goes deepest first,
    /// so that folder stays and so does every folder above it. The defect in issue #118 was that the
    /// removal recorded none of it: a tree left standing up to the scratch folder reported nothing.
    /// Now it names every folder it could not take, and counts the one Windows refused for itself.
    /// </summary>
    [Fact]
    public async Task RecordsAFolderAProgramIsWorkingInAndTheFoldersItHoldsUp()
    {
        var root = _temp.CreateDirectory("scratch");
        var live = _temp.CreateDirectory("scratch", "live");
        var session = _temp.CreateDirectory("scratch", "live", "session");
        var working = _temp.CreateDirectory("scratch", "live", "session", "work");
        _temp.CreateFile(1024, "scratch", "live", "session", "work", "state.bin");
        _temp.CreateFile(512, "scratch", "abandoned.tmp");

        RemovalOutcome outcome;

        using (new HeldDirectory(working))
        {
            outcome = await DirectoryRemover.RemoveAsync(
                root, MinimumAge.Off, progress: null, default, fileSystem: null, new RemovalBounds(KeepRoot: true, []));
        }

        Assert.Equal(1024 + 512, outcome.BytesReclaimed);
        Assert.Equal(new FolderRefusals(InUse: 1, Denied: 0), outcome.RefusedFolders);

        // Deepest first, as they were tried. The root was kept, so it was never tried at all.
        Assert.Equal([working, session, live], outcome.LeftStanding);
        Assert.True(Directory.Exists(working), "the fixture let a folder a program was working in go");
    }

    /// <summary>
    /// A folder still holding a refused file stays too, and is recorded as standing, but Windows did not
    /// refuse the folder. The file is already counted where it was refused, and counting the folders
    /// above it as well would tell the reader one open file kept a chain of folders.
    /// </summary>
    [Fact]
    public async Task RecordsAFolderAFileHoldsUpWithoutCountingItAsRefused()
    {
        var root = _temp.CreateDirectory("cache");
        var packages = Path.Combine(root, "packages");
        var held = _temp.CreateFile(2048, "cache", "packages", "held.nupkg");

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = await DirectoryRemover.RemoveAsync(root);

            Assert.Equal(new RefusalTally(1, 2048), outcome.Refused.InUse);
            Assert.True(outcome.RefusedFolders.IsEmpty);
            Assert.Equal([packages, root], outcome.LeftStanding);
        }
    }

    /// <summary>
    /// A folder Windows denies is counted as denied, never as in use. Windows reports that denial for a
    /// folder as a bare IOException, the same exception type a folder in use throws, so the reason has to
    /// come from the error the exception carries.
    /// </summary>
    [Fact]
    public async Task CountsAFolderWindowsDeniesAsDeniedRatherThanInUse()
    {
        var root = _temp.CreateDirectory("scratch");
        var guarded = _temp.CreateDirectory("scratch", "guarded");

        var fs = new RefusingFileSystem(
            WindowsFileSystem.Default,
            new Dictionary<string, RefusalReason> { [guarded] = RefusalReason.Denied });

        var outcome = await DirectoryRemover.RemoveAsync(
            root, MinimumAge.Off, progress: null, default, fs, new RemovalBounds(KeepRoot: true, []));

        Assert.Equal(new FolderRefusals(InUse: 0, Denied: 1), outcome.RefusedFolders);
        Assert.Equal([guarded], outcome.LeftStanding);
        Assert.True(Directory.Exists(guarded), "the fixture let a denied folder go");
    }

    [Fact]
    public async Task ClearsTheReadOnlyBitThatPackageCachesSetLiberally()
    {
        var root = _temp.CreateDirectory("cache");
        var file = _temp.CreateFile(512, "cache", "locked.bin");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.True(outcome.RootRemoved);
        Assert.Equal(512, outcome.BytesReclaimed);
    }

    /// <summary>
    /// The same bit, on a directory rather than a file.
    ///
    /// Windows refuses to remove a directory carrying <c>FILE_ATTRIBUTE_READONLY</c>, exactly as it
    /// refuses a read-only file — observed directly on this machine, where
    /// <c>Directory.Delete</c> on one throws <see cref="UnauthorizedAccessException"/>. The file
    /// path has always cleared the bit and retried; the directory path did not, so every file in
    /// the tree went and the directory stayed. The step then reports success, because bytes were
    /// reclaimed, and the folder the user was told would go is still on the disk.
    /// </summary>
    [Fact]
    public async Task RemovesADirectoryWhoseReadOnlyBitWouldOtherwiseBlockIt()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(1024, "cache", "held", "payload.bin");

        var held = Path.Combine(root, "held");
        File.SetAttributes(held, File.GetAttributes(held) | FileAttributes.ReadOnly);

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.Equal(1024, outcome.BytesReclaimed);
        Assert.False(Directory.Exists(held), "a read-only directory survived the removal");
        Assert.True(outcome.RootRemoved);
    }

    /// <summary>
    /// The other half of the same retry, and the reason it is gated on emptiness. A directory that
    /// still holds something is one the removal is leaving standing, so its attributes are left
    /// exactly as they were rather than reset on a path that survives.
    /// </summary>
    [Fact]
    public async Task LeavesAReadOnlyDirectoryUntouchedWhileSomethingInsideItIsStillHeld()
    {
        var root = _temp.CreateDirectory("cache");
        var held = _temp.CreateFile(2048, "cache", "kept", "held.bin");
        var directory = Path.Combine(root, "kept");
        File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.ReadOnly);

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await DirectoryRemover.RemoveAsync(root);

            Assert.True(Directory.Exists(directory));
            Assert.True(
                File.GetAttributes(directory).HasFlag(FileAttributes.ReadOnly),
                "a directory that survived the removal had its attributes reset");
        }

        File.SetAttributes(directory, FileAttributes.Normal);
    }

    [Fact]
    public async Task LeavesAFileHeldOpenInPlaceRatherThanFailingTheRun()
    {
        // §5.3: a locked file is the OS protecting live state. Skipping is the correct outcome.
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(1024, "cache", "free.bin");
        var held = _temp.CreateFile(2048, "cache", "held.bin");

        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = await DirectoryRemover.RemoveAsync(root);

            // In use, with its size, and not denied: the reader answers the first by closing a
            // program, and nothing answers the second.
            Assert.Equal(new RefusalTally(1, 2048), outcome.Refused.InUse);
            Assert.Equal(default, outcome.Refused.Denied);
            Assert.Equal([held], outcome.RefusedAt);

            Assert.Equal(1024, outcome.BytesReclaimed);
            Assert.False(outcome.RootRemoved);
            Assert.True(File.Exists(held));
        }
    }

    /// <summary>
    /// A file Windows denies is reported as denied, with its size — never as "in use".
    ///
    /// <para>The defect in issue #117: Windows refused every file in guarded browser
    /// profiles, the removal counted each as a skip, and the run said a quarter of a million items
    /// were "in use" about 5.9 GB nothing would ever release. The read-only retry is what makes the
    /// classification easy to get wrong, because the first refusal is always an access refusal —
    /// the reason has to come from the second attempt.</para>
    /// </summary>
    [Fact]
    public async Task ReportsAFileWindowsDeniesAsDeniedRatherThanInUse()
    {
        var root = _temp.CreateDirectory("scratch");
        var denied = _temp.CreateFile(4096, "scratch", "profile", "Default", "Cookies");
        _temp.CreateFile(1024, "scratch", "abandoned.tmp");

        var fs = new RefusingFileSystem(
            WindowsFileSystem.Default,
            new Dictionary<string, RefusalReason> { [denied] = RefusalReason.Denied });

        var outcome = await DirectoryRemover.RemoveAsync(
            root, MinimumAge.Off, progress: null, default, fs, new RemovalBounds(KeepRoot: true, []));

        Assert.Equal(new RefusalTally(1, 4096), outcome.Refused.Denied);
        Assert.Equal(default, outcome.Refused.InUse);
        Assert.Equal(1024, outcome.BytesReclaimed);

        Assert.True(File.Exists(denied), "the fixture let a denied file go");
        Assert.False(File.Exists(Path.Combine(root, "abandoned.tmp")), "a refusal stopped the rest of the clear");
    }

    /// <summary>
    /// The place a refusal is recorded against is the entry directly under the root, however deep the
    /// refused file sits. That entry is what the next preview asks about again, so recording the
    /// file's own directory would miss its siblings, and recording the root would open every file in
    /// the tree for deletion on every preview.
    /// </summary>
    [Fact]
    public async Task RecordsARefusalAgainstTheEntryDirectlyUnderTheRoot()
    {
        var root = _temp.CreateDirectory("scratch");
        var profile = Path.Combine(root, "profile");
        var deep = _temp.CreateFile(64, "scratch", "profile", "Default", "Cache", "data_0");
        var beside = _temp.CreateFile(64, "scratch", "held.log");

        var fs = new RefusingFileSystem(
            WindowsFileSystem.Default,
            new Dictionary<string, RefusalReason>
            {
                [deep] = RefusalReason.Denied,
                [beside] = RefusalReason.InUse,
            });

        var outcome = await DirectoryRemover.RemoveAsync(
            root, MinimumAge.Off, progress: null, default, fs, new RemovalBounds(KeepRoot: true, []));

        Assert.Equal([beside, profile], outcome.RefusedAt);
    }

    /// <summary>
    /// A read-only link is removed, and the question that would have read through it is never asked.
    ///
    /// Windows refuses to remove a link carrying the read-only bit exactly as it refuses a real
    /// directory, and clearing that bit acts on the link rather than on what it points at — so the
    /// retry is right here. What must not happen is the emptiness check: enumerating a link reads
    /// the far side, which nothing in this removal has classified, and the answer would then decide
    /// the fate of a path in a tree nobody looked at.
    /// </summary>
    [Fact]
    public async Task RemovesAReadOnlyLinkWithoutAskingWhatIsOnTheFarSideOfIt()
    {
        var root = _temp.CreateDirectory("cache");
        var outside = _temp.CreateDirectory("precious");
        var bystander = _temp.CreateFile(4096, "precious", "irreplaceable.bin");

        var link = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(link, outside);
        File.SetAttributes(link, File.GetAttributes(link) | FileAttributes.ReadOnly);

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.True(outcome.RootRemoved);
        Assert.False(Directory.Exists(link), "a read-only link survived, so the far side decided its fate");

        Assert.True(Directory.Exists(outside), "removal followed the link it was handed");
        Assert.True(File.Exists(bystander));
        Assert.Equal(0, outcome.BytesReclaimed);
    }

    /// <summary>
    /// A smoke test, and deliberately no more than that. It cannot prove §6.3 — see
    /// <see cref="HandsEveryPathToTheFilesystemInExtendedLengthForm"/> for the assertion that can,
    /// and for why this one stays green with the prefixing removed outright.
    /// </summary>
    [Fact]
    public async Task ReachesFilesBeyondMaxPath()
    {
        var root = _temp.CreateDirectory("cache");

        var deep = root;
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('n', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "payload.bin")), new byte[8192]);

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.Equal(8192, outcome.BytesReclaimed);
        Assert.True(outcome.RootRemoved);
    }

    /// <summary>
    /// §6.3 — the assertion that actually discriminates.
    ///
    /// Removing a tree past MAX_PATH and watching it disappear proves nothing about this codebase.
    /// .NET's own path normalisation prepends <c>\\?\</c> to any path of 260 characters or more
    /// before it reaches Win32, so the deletion succeeds whether or not Core applied the prefix —
    /// measured directly: a raw <c>CreateDirectoryW</c> on such a path fails with
    /// ERROR_PATH_NOT_FOUND while <c>Directory.CreateDirectory</c> on the very same path succeeds,
    /// in a process where <c>RtlAreLongPathsEnabled</c> reports 0. That makes an outcome-based
    /// long-path test unfalsifiable on every machine, not merely on one with the
    /// <c>LongPathsEnabled</c> registry value set.
    ///
    /// The form of the path is what remains observable, and it discriminates everywhere. The tree
    /// below covers each branch that touches the filesystem: enumeration, a plain delete, the
    /// read-only retry, directory removal, and the reparse point.
    /// </summary>
    [Fact]
    public async Task HandsEveryPathToTheFilesystemInExtendedLengthForm()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(1024, "cache", "a.bin");
        _temp.CreateFile(2048, "cache", "nested", "b.bin");

        var readOnly = _temp.CreateFile(512, "cache", "read-only.bin");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        Directory.CreateSymbolicLink(Path.Combine(root, "link"), _temp.CreateDirectory("outside"));

        var deep = root;
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, new string('n', 40));
        }

        Directory.CreateDirectory(LongPath.Extended(deep));
        File.WriteAllBytes(LongPath.Extended(Path.Combine(deep, "payload.bin")), new byte[4096]);

        var recorder = new RecordingFileSystem(WindowsFileSystem.Default);
        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.Off, progress: null, default, recorder);

        Assert.True(outcome.RootRemoved);
        Assert.NotEmpty(recorder.Paths);
        Assert.All(
            recorder.Paths,
            path => Assert.StartsWith(@"\\?\", path, StringComparison.Ordinal));
    }

    /// <summary>
    /// The same rule applied to the root itself, which is the one entry no enumeration classifies.
    /// <see cref="DeletesAJunctionWithoutFollowingItIntoTheTargetTree"/> covers a junction found
    /// below the root; a junction handed in *as* the root took the other branch, where enumerating
    /// it transparently returns the link target's ordinary children and deletes them.
    /// </summary>
    [Fact]
    public async Task RemovesAJunctionGivenAsTheRootWithoutEmptyingWhatItPointsAt()
    {
        var outside = _temp.CreateDirectory("precious");
        var bystander = _temp.CreateFile(4096, "precious", "irreplaceable.bin");

        var junction = Path.Combine(_temp.Path, "cache");
        Directory.CreateSymbolicLink(junction, outside);

        var outcome = await DirectoryRemover.RemoveAsync(junction);

        Assert.True(outcome.RootRemoved);
        Assert.False(Directory.Exists(junction));

        Assert.True(Directory.Exists(outside), "removal followed the junction it was handed");
        Assert.True(File.Exists(bystander), "a file outside the target tree was destroyed");

        // The linked-to content was never ours to count.
        Assert.Equal(0, outcome.BytesReclaimed);
    }

    [Fact]
    public async Task DeletesAJunctionWithoutFollowingItIntoTheTargetTree()
    {
        // The highest-consequence branch in the codebase: if this regresses, deleting a cache
        // escapes through a junction and destroys whatever it points at.
        var root = _temp.CreateDirectory("cache");
        var outside = _temp.CreateDirectory("precious");
        var bystander = _temp.CreateFile(4096, "precious", "irreplaceable.bin");

        var junction = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(junction, outside);

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.True(outcome.RootRemoved);
        Assert.False(Directory.Exists(junction));

        Assert.True(Directory.Exists(outside), "deletion followed the junction out of the target tree");
        Assert.True(File.Exists(bystander), "a file outside the target tree was destroyed");

        // The linked-to content was never ours to count.
        Assert.Equal(0, outcome.BytesReclaimed);
    }

    [Fact]
    public async Task StopsWhenCancelled()
    {
        var root = _temp.CreateDirectory("cache");
        for (var i = 0; i < 200; i++)
        {
            _temp.CreateFile(64, "cache", $"f{i}.bin");
        }

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DirectoryRemover.RemoveAsync(root, MinimumAge.Off, progress: null, cts.Token));
    }

    [Fact]
    public async Task ReportsProgressThroughToCompletion()
    {
        var root = _temp.CreateDirectory("cache");
        for (var i = 0; i < 8; i++)
        {
            _temp.CreateFile(64, "cache", $"f{i}.bin");
        }

        var reported = new List<double>();
        await DirectoryRemover.RemoveAsync(root, MinimumAge.Off, new Progress<double>(reported.Add));

        // Progress is marshalled asynchronously, so only the terminal report is guaranteed —
        // asserting on intermediate values here would be a flaky test.
        Assert.True(Directory.Exists(root) is false);
        Assert.All(reported, value => Assert.InRange(value, 0.0, 1.0));
    }

    /// <summary>
    /// The guard the user set, applied where a deletion actually happens.
    ///
    /// §5.6's negative is the whole assertion here: proving the stale file went is half a test, and
    /// the half that cannot fail. What has to hold is that the recent file is still on the disk
    /// afterwards, and that the folder around it stayed with it.
    /// </summary>
    [Fact]
    public async Task LeavesARecentFileAndTheFolderHoldingItWhereTheyAre()
    {
        var root = _temp.CreateDirectory("cache");
        var stale = TempDirectory.Age(_temp.CreateFile(1024, "cache", "old", "a.bin"), TimeSpan.FromDays(30));
        var recent = _temp.CreateFile(2048, "cache", "live", "scratch.bin");

        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.True(File.Exists(recent), "a file inside the guard window was deleted");
        Assert.True(Directory.Exists(Path.GetDirectoryName(recent)!), "the folder holding it was removed");
        Assert.True(Directory.Exists(root), "the root went, so the kept file was orphaned or lost");

        Assert.False(File.Exists(stale), "a file well outside the window was kept");
        Assert.False(Directory.Exists(Path.GetDirectoryName(stale)!));

        Assert.Equal(1024, outcome.BytesReclaimed);
        Assert.Equal(1, outcome.Kept);
        Assert.True(outcome.Refused.IsEmpty);
        Assert.False(outcome.RootRemoved);
    }

    /// <summary>
    /// §9: an Outlook mail store is never removed, wherever a removal meets one. The negative is the
    /// assertion: the store, and every folder holding it, are still on the disk afterwards, while
    /// everything around them — a name that only resembles a store among them — is gone.
    /// </summary>
    [Fact]
    public async Task LeavesAnOutlookDataFileAndEveryFolderHoldingIt()
    {
        var root = _temp.CreateDirectory("scratch");
        var archive = _temp.CreateFile(4096, "scratch", "extracted", "mail", "archive.pst");
        var mailbox = _temp.CreateFile(2048, "scratch", "someone@example.com.OST");
        var lookalike = _temp.CreateFile(512, "scratch", "extracted", "archive.pst.txt");
        var ordinary = _temp.CreateFile(1024, "scratch", "abandoned.tmp");

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.True(File.Exists(archive), "an Outlook data file was deleted");
        Assert.True(File.Exists(mailbox), "an offline mailbox was deleted");
        Assert.True(Directory.Exists(Path.GetDirectoryName(archive)!), "the folder holding a data file was removed");
        Assert.True(Directory.Exists(root), "the root went, so a data file inside it was lost");
        Assert.False(outcome.RootRemoved);

        Assert.False(File.Exists(lookalike), "a name that only resembles a data file was kept");
        Assert.False(File.Exists(ordinary));
        Assert.Equal(512 + 1024, outcome.BytesReclaimed);

        Assert.Equal(
            [archive, mailbox],
            outcome.MailStores.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(0, outcome.Kept);
        Assert.True(outcome.Refused.IsEmpty);
    }

    /// <summary>
    /// A store the guard would also have kept is reported as a store. The rule is unconditional and
    /// the guard is a preference, so the sentence that reaches the reader has to name the rule — the
    /// guard's "changed recently" would tell them it goes once it is old enough.
    /// </summary>
    [Fact]
    public async Task AStoreTheGuardWouldAlsoKeepIsCountedAsAStore()
    {
        var root = _temp.CreateDirectory("cache");
        var archive = _temp.CreateFile(4096, "cache", "written-just-now.pst");

        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.True(File.Exists(archive));
        Assert.Equal([archive], outcome.MailStores);
        Assert.Equal(0, outcome.Kept);
    }

    /// <summary>
    /// The store question is asked before the link question, because the mark Windows puts on a link
    /// is also on files that are not links: a OneDrive placeholder and a deduplicated file carry it,
    /// and deleting either deletes its content. Nothing here reads which kind of mark a file carries,
    /// so a file named like a store is left whatever it turns out to be. A real link to a store is left
    /// too, which costs nothing — and it is the real link this test can make on any machine.
    /// </summary>
    [Fact]
    public async Task LeavesAFileNamedLikeAStoreEvenWhereItCarriesTheMarkOfALink()
    {
        var root = _temp.CreateDirectory("scratch");
        var target = _temp.CreateFile(4096, "elsewhere", "archive.pst");
        var abandoned = _temp.CreateFile(1024, "scratch", "abandoned.tmp");
        var link = Path.Combine(root, "shortcut.pst");

        File.CreateSymbolicLink(link, target);

        var outcome = await DirectoryRemover.RemoveAsync(root);

        Assert.True(LongPath.IsReparsePoint(link), "a file carrying a link's mark and named like a store was removed");
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(abandoned));
        Assert.Equal([link], outcome.MailStores);
        Assert.False(outcome.RootRemoved);
    }

    /// <summary>
    /// Kept and skipped are counted apart because they are different sentences to the reader: one is
    /// Windows refusing, which they can act on, and the other is a setting they chose.
    /// </summary>
    [Fact]
    public async Task CountsWhatTheGuardKeptSeparatelyFromWhatWasInUse()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(64, "cache", "fresh-one.bin");
        _temp.CreateFile(64, "cache", "fresh-two.bin");
        TempDirectory.Age(_temp.CreateFile(4096, "cache", "stale.bin"), TimeSpan.FromDays(2));

        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.WithinHours(1, DateTime.UtcNow));

        Assert.Equal(2, outcome.Kept);
        Assert.True(outcome.Refused.IsEmpty);
        Assert.Equal(4096, outcome.BytesReclaimed);
    }

    /// <summary>
    /// With no guard set, the removal is the one it always was. Every seam defaults its guard
    /// parameter, so this is the behaviour every existing caller still gets.
    /// </summary>
    [Fact]
    public async Task RemovesEverythingWhenNoGuardIsSet()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(2048, "cache", "written-just-now.bin");

        var outcome = await DirectoryRemover.RemoveAsync(root, MinimumAge.Off);

        Assert.True(outcome.RootRemoved);
        Assert.Equal(0, outcome.Kept);
        Assert.Equal(2048, outcome.BytesReclaimed);
    }

    /// <summary>
    /// The bound <c>%TEMP%</c> exists for: the contents go and the folder stays. Windows does not
    /// put a deleted temporary folder back, so a removal that took it would break the next
    /// installer on the machine.
    /// </summary>
    [Fact]
    public async Task KeepsTheRootWhereTheBoundsSayToAndStillEmptiesIt()
    {
        var root = _temp.CreateDirectory("scratch");
        _temp.CreateFile(1024, "scratch", "a.tmp");
        _temp.CreateFile(2048, "scratch", "nested", "b.tmp");

        var outcome = await DirectoryRemover.RemoveAsync(
            root, bounds: new RemovalBounds(KeepRoot: true, []));

        Assert.True(Directory.Exists(root), "the folder the bounds said to keep was removed");
        Assert.False(outcome.RootRemoved);
        Assert.Equal(1024 + 2048, outcome.BytesReclaimed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    /// <summary>
    /// §5.3's second exclusion, and the one an age cannot express: an entry a program is working in
    /// stays, however old everything inside it is.
    /// </summary>
    [Fact]
    public async Task LeavesASparedEntryAndEverythingUnderIt()
    {
        var root = _temp.CreateDirectory("scratch");
        var live = _temp.CreateDirectory("scratch", "live-session");
        TempDirectory.Age(_temp.CreateFile(4096, "scratch", "live-session", "deep", "notes.txt"), TimeSpan.FromDays(30));
        TempDirectory.Age(_temp.CreateFile(1024, "scratch", "abandoned.tmp"), TimeSpan.FromDays(30));

        var outcome = await DirectoryRemover.RemoveAsync(
            root, bounds: new RemovalBounds(KeepRoot: true, [live]));

        Assert.True(File.Exists(Path.Combine(live, "deep", "notes.txt")), "a spared entry was emptied");
        Assert.Equal(1, outcome.Spared);
        Assert.Equal(1024, outcome.BytesReclaimed);
        Assert.False(File.Exists(Path.Combine(root, "abandoned.tmp")));
    }

    /// <summary>
    /// §6.3 on the comparison rather than on a deletion, which is the only place it can be observed
    /// here: an enumeration below an extended root yields extended children, so a spared set holding
    /// display paths would match none of them.
    ///
    /// <para>That failure is silent and runs in the dangerous direction — every spared entry would
    /// be deleted, and the outcome would look exactly like a successful clear. The assertion is
    /// therefore on what survived a spare declared in display form, which is the form a plan carries.
    /// Stripping <c>LongPath.Extended</c> from <c>RemovalBounds.SparedPaths</c> fails this and
    /// nothing else in the suite.</para>
    /// </summary>
    [Fact]
    public async Task MatchesASparedPathDeclaredInDisplayFormAgainstTheExtendedPathsTheWalkSees()
    {
        var root = _temp.CreateDirectory("scratch");
        TempDirectory.Age(_temp.CreateFile(512, "scratch", "held", "payload.bin"), TimeSpan.FromDays(30));

        var spared = Path.Combine(root, "held");
        Assert.DoesNotContain(@"\\?\", spared, StringComparison.Ordinal);

        var outcome = await DirectoryRemover.RemoveAsync(
            root, bounds: new RemovalBounds(KeepRoot: true, [spared]));

        Assert.Equal(1, outcome.Spared);
        Assert.True(File.Exists(Path.Combine(spared, "payload.bin")), "the spared entry was deleted");
        Assert.Equal(0, outcome.BytesReclaimed);
    }

    /// <summary>
    /// A spared entry is spared whatever it turns out to be, links included. Removing the link is
    /// still taking the scratch directory away from whatever was handed it.
    /// </summary>
    [Fact]
    public async Task LeavesASparedEntryThatTurnedOutToBeALink()
    {
        var root = _temp.CreateDirectory("scratch");
        var target = _temp.CreateDirectory("elsewhere");
        var link = Path.Combine(root, "session");

        Directory.CreateSymbolicLink(link, target);

        var outcome = await DirectoryRemover.RemoveAsync(
            root, bounds: new RemovalBounds(KeepRoot: true, [link]));

        Assert.Equal(1, outcome.Spared);
        Assert.True(Directory.Exists(link), "a spared link was removed");
    }

    /// <summary>
    /// A temporary folder that is itself a junction is left entirely alone, rather than having the
    /// link removed as an ordinary removal would.
    ///
    /// Removing it would destroy the very path the caller said must survive, and following it would
    /// empty a tree nobody classified — the vacuous §5.6 negative, since every survivor named for
    /// that root resolves through the link.
    /// </summary>
    [Fact]
    public async Task RemovesNothingWhenTheRootIsALinkAndTheBoundsSayToKeepIt()
    {
        var target = _temp.CreateDirectory("elsewhere");
        _temp.CreateFile(2048, "elsewhere", "payload.bin");

        var link = Path.Combine(_temp.Path, "scratch");
        Directory.CreateSymbolicLink(link, target);

        var outcome = await DirectoryRemover.RemoveAsync(
            link, bounds: new RemovalBounds(KeepRoot: true, []));

        Assert.True(Directory.Exists(link), "the link the caller said to keep was removed");
        Assert.True(File.Exists(Path.Combine(target, "payload.bin")), "the removal followed a link");
        Assert.Equal(0, outcome.BytesReclaimed);
        Assert.False(outcome.RootRemoved);
    }

    /// <summary>
    /// The guard covers a link exactly as it covers a file.
    ///
    /// <para>The link branch deletes, and it used to sit in front of the guard — so a junction made
    /// a minute ago was removed under a plan promising nothing touched in the last week would be.
    /// It failed silently in both directions that hide it: a link's length is zero, so no size
    /// moved, and it was counted as neither kept nor skipped, so no count moved either.</para>
    ///
    /// <para>A link carries its own timestamps, so this is a question about the link rather than
    /// about whatever it points at — which is right, because removing the link is what takes the
    /// scratch directory away from the program that was handed it.</para>
    /// </summary>
    /// <para>Only the protected direction is asserted here. A link is created with the clock's
    /// current time and its timestamps cannot be moved back through
    /// <see cref="TempDirectory.Age"/> — Windows refuses to open a directory link for a
    /// write-attributes handle — so "an old link still goes" is left to
    /// <see cref="DeletesAJunctionWithoutFollowingItIntoTheTargetTree"/>, which removes one under
    /// no guard at all.</para>
    [Fact]
    public async Task LeavesALinkTheGuardProtects()
    {
        var root = _temp.CreateDirectory("scratch");
        var target = _temp.CreateDirectory("elsewhere");
        _temp.CreateFile(2048, "elsewhere", "payload.bin");

        var link = Path.Combine(root, "just-made");
        Directory.CreateSymbolicLink(link, target);

        var outcome = await DirectoryRemover.RemoveAsync(
            root, MinimumAge.WithinHours(8, DateTime.UtcNow), bounds: new RemovalBounds(KeepRoot: true, []));

        Assert.True(Directory.Exists(link), "a link made a moment ago was deleted under a guard");
        Assert.Equal(1, outcome.Kept);

        // Counted as held back by the guard rather than as skipped, because that is what happened:
        // Windows refused nothing.
        Assert.True(outcome.Refused.IsEmpty);
        Assert.True(File.Exists(Path.Combine(target, "payload.bin")), "the removal followed a link");
    }

    /// <summary>
    /// The guard covers a link that is the removal's own root, not only one found inside a tree.
    ///
    /// <para>The root is the one entry no enumeration classified, so it takes its own question — the
    /// same one <see cref="Execution.FileRemover"/> re-asks of the single path it is given. Without
    /// it a cache directory somebody had just relocated with <c>mklink</c> was removed under a plan
    /// promising nothing touched in the last eight hours would be, and the outcome reported success
    /// because a link's length is zero.</para>
    /// </summary>
    [Fact]
    public async Task LeavesARootThatIsALinkTheGuardProtects()
    {
        var target = _temp.CreateDirectory("elsewhere");
        _temp.CreateFile(2048, "elsewhere", "payload.bin");

        var link = Path.Combine(_temp.Path, "relocated-cache");
        Directory.CreateSymbolicLink(link, target);

        var outcome = await DirectoryRemover.RemoveAsync(link, MinimumAge.WithinHours(8, DateTime.UtcNow));

        Assert.True(Directory.Exists(link), "a link made a moment ago was removed under a guard");
        Assert.False(outcome.RootRemoved);
        Assert.Equal(1, outcome.Kept);
        Assert.True(File.Exists(Path.Combine(target, "payload.bin")), "the removal followed a link");
    }

    /// <summary>
    /// A folder that is not there is not the success it is for a deletion: the caller asked for a
    /// directory that is meant to still exist, and it does not.
    /// </summary>
    [Fact]
    public async Task DoesNotCallAMissingFolderRemovedWhenItWasMeantToStay()
    {
        var outcome = await DirectoryRemover.RemoveAsync(
            Path.Combine(_temp.Path, "never-existed"),
            bounds: new RemovalBounds(KeepRoot: true, []));

        Assert.False(outcome.RootRemoved);
    }
}
