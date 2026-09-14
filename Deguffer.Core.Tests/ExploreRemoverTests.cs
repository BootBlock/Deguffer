using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// What an Explore removal does, and the §5.6 evidence that it did no more.
///
/// <para>Every removal here happens inside a synthetic profile on a synthetic volume, so the
/// refusals can be driven against a Windows directory the test built rather than the real one.</para>
/// </summary>
public sealed class ExploreRemoverTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeSystemDirectories _system;
    private readonly FakeUserEnvironment _environment;
    private readonly ExploreActionPolicy _policy;

    public ExploreRemoverTests()
    {
        _system = new FakeSystemDirectories(_temp.Path);
        _environment = new FakeUserEnvironment(_temp.Path);

        // The region table alone: these tests are about what the remover does with a verdict, and no
        // provider takes part in any of them. ExploreActionPolicy.ForAsync is asserted where §5.2's
        // declarations are, and it cannot be awaited in a constructor.
        _policy = new ExploreActionPolicy(ProtectedRegions.For(_system, _environment), []);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// §7.1: removal from Explore goes to the Recycle Bin by default. The one file a user picked out
    /// of a picture is exactly the case where recovery is available, and where it is available it is
    /// not optional.
    /// </summary>
    [Fact]
    public async Task TheRecycleBinIsTheDefaultRoute()
    {
        var file = _temp.CreateFile(64, "profile", "Downloads", "big.bin");
        var bin = new FakeRecycleBin();

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(file, IsDirectory: false, Bytes: 64)],
            ExploreRemovalMode.RecycleBin,
            _policy,
            bin);

        Assert.Single(report.Removed);
        Assert.Equal(64, report.BytesRemoved);
        Assert.False(LongPath.FileExists(file));
        Assert.Equal(file, Assert.Single(bin.Paths));
    }

    /// <summary>
    /// §6.3, at the one boundary in Core that requires the <em>opposite</em> form from all the
    /// others: the shell namespace refuses <c>\\?\</c>, so what crosses here is the display path —
    /// but still fully qualified and fully resolved, because a value carrying a <c>.</c> or
    /// <c>..</c> segment would recycle a directory nobody named.
    ///
    /// <para>Asserted on the form of the path handed across rather than on the outcome, because the
    /// outcome cannot tell the two apart: Windows resolves both, so a naive implementation passing
    /// its argument straight through recycles the right file and proves nothing.</para>
    /// </summary>
    [Fact]
    public async Task TheShellIsHandedANormalisedDisplayPath()
    {
        var file = _temp.CreateFile(8, "profile", "Downloads", "big.bin");
        var awkward = Path.Combine(_temp.Path, "profile", "Downloads", ".", "big.bin");
        var bin = new FakeRecycleBin();

        await ExploreRemover.RemoveAsync(
            [new ExploreItem(awkward, IsDirectory: false, Bytes: 8)],
            ExploreRemovalMode.RecycleBin,
            _policy,
            bin);

        var handed = Assert.Single(bin.Paths);

        Assert.DoesNotContain(@"\\?\", handed, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\.\", handed, StringComparison.Ordinal);
        Assert.Equal(file, handed);
    }

    /// <summary>
    /// §6.3 for the other route. The permanent removal goes through the ordinary remover, so every
    /// path it hands the filesystem carries the extended-length prefix.
    /// </summary>
    [Fact]
    public async Task ThePermanentRouteHandsTheFilesystemExtendedPaths()
    {
        var file = _temp.CreateFile(16, "profile", "Downloads", "big.bin");
        var recorder = new RecordingFileSystem(WindowsFileSystem.Default);

        await ExploreRemover.RemoveAsync(
            [new ExploreItem(file, IsDirectory: false, Bytes: 16)],
            ExploreRemovalMode.Permanent,
            _policy,
            recycleBin: null,
            fileSystem: recorder);

        Assert.NotEmpty(recorder.Paths);
        Assert.All(recorder.Paths, p => Assert.StartsWith(@"\\?\", p, StringComparison.Ordinal));
    }

    [Fact]
    public async Task APermanentRemovalDeletesTheWholeTree()
    {
        var folder = _temp.CreateDirectory("profile", "Downloads", "junk");
        _temp.CreateFile(32, "profile", "Downloads", "junk", "a.bin");
        _temp.CreateFile(32, "profile", "Downloads", "junk", "nested", "b.bin");

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(folder, IsDirectory: true, Bytes: 64)],
            ExploreRemovalMode.Permanent,
            _policy);

        Assert.Single(report.Removed);
        Assert.False(LongPath.DirectoryExists(folder));
        Assert.Equal(64, report.BytesRemoved);
    }

    /// <summary>
    /// A file left in place says which kind of refusal left it. The old sentence said a file held open
    /// and one this account may not touch "are the same answer from here", and the removal can now
    /// tell them apart: the reader answers the first by closing a program.
    /// </summary>
    [Fact]
    public async Task SaysAnotherProgramHadAFileOpenWhenThatIsWhatLeftItInPlace()
    {
        var file = _temp.CreateFile(16, "profile", "Downloads", "open.bin");
        ExploreRemovalReport report;

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            report = await ExploreRemover.RemoveAsync(
                [new ExploreItem(file, IsDirectory: false, Bytes: 16)],
                ExploreRemovalMode.Permanent,
                _policy);
        }

        Assert.Contains("another program had 1 file(s)", Assert.Single(report.Refused).Message, StringComparison.Ordinal);
        Assert.True(LongPath.FileExists(file));
    }

    /// <summary>
    /// And a folder that could only be partly deleted says Windows denied what it still holds, rather
    /// than calling it "in use" — which is what sent a reader looking for a program that was not there.
    /// </summary>
    [Fact]
    public async Task SaysWindowsDeniedWhatAPartlyDeletedFolderStillHolds()
    {
        var folder = _temp.CreateDirectory("profile", "Downloads", "junk");
        _temp.CreateFile(32, "profile", "Downloads", "junk", "a.bin");
        var denied = _temp.CreateFile(32, "profile", "Downloads", "junk", "guarded.bin");

        var fs = new RefusingFileSystem(
            WindowsFileSystem.Default,
            new Dictionary<string, RefusalReason> { [denied] = RefusalReason.Denied });

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(folder, IsDirectory: true, Bytes: 64)],
            ExploreRemovalMode.Permanent,
            _policy,
            recycleBin: null,
            fileSystem: fs);

        var message = Assert.Single(report.Refused).Message;

        Assert.Contains("Windows would not let Deguffer remove them", message, StringComparison.Ordinal);
        Assert.DoesNotContain("in use", message, StringComparison.OrdinalIgnoreCase);
        Assert.True(LongPath.FileExists(denied));
    }

    /// <summary>
    /// A folder a program is working in keeps the item around it standing, and the sentence says so.
    /// Without it the item was "partly deleted" for no stated reason, and the reader had no way to know
    /// that closing a program would let the rest go.
    /// </summary>
    [Fact]
    public async Task SaysAnotherProgramWasUsingAFolderThatKeptAPartlyDeletedItem()
    {
        var folder = _temp.CreateDirectory("profile", "Downloads", "junk");
        var working = _temp.CreateDirectory("profile", "Downloads", "junk", "work");
        _temp.CreateFile(32, "profile", "Downloads", "junk", "a.bin");

        ExploreRemovalReport report;

        using (new HeldDirectory(working))
        {
            report = await ExploreRemover.RemoveAsync(
                [new ExploreItem(folder, IsDirectory: true, Bytes: 32)],
                ExploreRemovalMode.Permanent,
                _policy);
        }

        Assert.Equal(
            "Partly deleted, 1 folder(s) left in place because another program was using them, so the folder is still there.",
            Assert.Single(report.Refused).Message);
        Assert.True(LongPath.DirectoryExists(working));
    }

    /// <summary>
    /// The negative that matters. A refused item is not merely absent from the report — it is still
    /// on the disk, and the shell is never asked about it.
    /// </summary>
    [Fact]
    public async Task ARefusedItemStaysOnTheDisk()
    {
        var inside = Directory.CreateDirectory(
            Path.Combine(_system.WindowsDirectory, "System32")).FullName;
        var bin = new FakeRecycleBin();

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(inside, IsDirectory: true, Bytes: 1024)],
            ExploreRemovalMode.RecycleBin,
            _policy,
            bin);

        Assert.Empty(report.Removed);
        Assert.Empty(bin.Paths);
        Assert.True(LongPath.DirectoryExists(inside));
        Assert.Equal(0, report.BytesRemoved);
        Assert.Contains("Windows directory", Assert.Single(report.Refused).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// §9's Outlook data file, picked out of the picture beside something ordinary, by either route.
    /// The ordinary file goes and the mail store does not: it is still on the disk, the shell was
    /// never asked about it, and the report quotes why rather than counting it.
    /// </summary>
    [Theory]
    [InlineData(ExploreRemovalMode.RecycleBin)]
    [InlineData(ExploreRemovalMode.Permanent)]
    public async Task APickedOutlookDataFileStaysOnTheDiskWhileWhatWasPickedBesideItGoes(ExploreRemovalMode mode)
    {
        var mailbox = _temp.CreateFile(64, "profile", "Downloads", "archive.pst");
        var ordinary = _temp.CreateFile(32, "profile", "Downloads", "big.bin");
        var bin = new FakeRecycleBin();

        var report = await ExploreRemover.RemoveAsync(
            [
                new ExploreItem(mailbox, IsDirectory: false, Bytes: 64),
                new ExploreItem(ordinary, IsDirectory: false, Bytes: 32),
            ],
            mode,
            _policy,
            bin);

        Assert.True(LongPath.FileExists(mailbox));
        Assert.False(LongPath.FileExists(ordinary));
        Assert.DoesNotContain(mailbox, bin.Paths);
        Assert.Equal(ordinary, Assert.Single(report.Removed).Path);
        Assert.Equal(mailbox, Assert.Single(report.Refused).Path);
        Assert.Contains("Outlook", report.Summary, StringComparison.Ordinal);
        Assert.True(report.Verification.Passed);
    }

    /// <summary>
    /// §9 one level up. A folder is not a store, so the policy allows it, and the shell moves a
    /// folder to the Recycle Bin whole — taking any store inside it, and putting it one emptied bin
    /// away from gone. So the remover looks inside first, and refuses with the store's name rather
    /// than asking the shell anything.
    /// </summary>
    [Fact]
    public async Task RefusesToMoveAFolderHoldingAStoreToTheRecycleBinAndSaysWhich()
    {
        var folder = _temp.CreateDirectory("profile", "Downloads", "old mail");
        var store = _temp.CreateFile(64, "profile", "Downloads", "old mail", "2014", "archive.pst");
        _temp.CreateFile(32, "profile", "Downloads", "old mail", "notes.txt");
        var bin = new FakeRecycleBin();

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(folder, IsDirectory: true, Bytes: 96)],
            ExploreRemovalMode.RecycleBin,
            _policy,
            bin);

        Assert.Empty(bin.Paths);
        Assert.True(LongPath.FileExists(store), "a folder holding a data file was moved to the Recycle Bin");
        Assert.True(LongPath.DirectoryExists(folder));

        var refused = Assert.Single(report.Refused);
        Assert.Contains(store, refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(report.Verification.Passed);
    }

    /// <summary>
    /// The permanent route can step over the store, so it does: everything else in the folder goes,
    /// the store and the folders holding it stay, and §5.6 proves the store is still there — which
    /// the siblings check alone never would, because the store is inside the item rather than beside
    /// it.
    /// </summary>
    [Fact]
    public async Task APermanentRemovalOfAFolderLeavesTheStoreInsideItAndProvesItStayed()
    {
        var folder = _temp.CreateDirectory("profile", "Downloads", "old mail");
        var store = _temp.CreateFile(64, "profile", "Downloads", "old mail", "2014", "archive.pst");
        var notes = _temp.CreateFile(32, "profile", "Downloads", "old mail", "notes.txt");

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(folder, IsDirectory: true, Bytes: 96)],
            ExploreRemovalMode.Permanent,
            _policy);

        Assert.True(LongPath.FileExists(store), "a data file inside the folder was deleted");
        Assert.True(LongPath.DirectoryExists(Path.GetDirectoryName(store)!));
        Assert.False(LongPath.FileExists(notes));

        var outcome = Assert.Single(report.Refused);
        Assert.Contains("1 Outlook data file(s) left alone", outcome.Message, StringComparison.Ordinal);

        Assert.Contains(report.Verification.Checks, check =>
            check.Subject.Equals(store, StringComparison.OrdinalIgnoreCase)
            && check.Outcome == VerificationOutcome.Survived);
        Assert.True(report.Verification.Passed);
    }

    /// <summary>The over-reach direction: a folder holding only a name that resembles a store is recycled as usual.</summary>
    [Fact]
    public async Task MovesAFolderHoldingOnlyALookalikeToTheRecycleBin()
    {
        var folder = _temp.CreateDirectory("profile", "Downloads", "exports");
        _temp.CreateFile(32, "profile", "Downloads", "exports", "archive.pst.txt");
        var bin = new FakeRecycleBin();

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(folder, IsDirectory: true, Bytes: 32)],
            ExploreRemovalMode.RecycleBin,
            _policy,
            bin);

        Assert.Equal(folder, Assert.Single(bin.Paths));
        Assert.Single(report.Removed);
    }

    /// <summary>
    /// A link is moved to the Recycle Bin as a link and takes nothing behind it, so a store on the far
    /// side does not stop it — and looking for one there would walk a tree nobody picked. The bin here
    /// only records, so the fixture cannot touch the far side whatever the shell would do.
    /// </summary>
    [Fact]
    public async Task MovesALinkToAFolderHoldingAStoreToTheRecycleBinAsTheLinkItIs()
    {
        var target = _temp.CreateDirectory("elsewhere", "mail");
        var store = _temp.CreateFile(64, "elsewhere", "mail", "archive.pst");
        var link = Path.Combine(_temp.CreateDirectory("profile", "Downloads"), "mail shortcut");

        Directory.CreateSymbolicLink(link, target);

        var bin = new FakeRecycleBin(_ => new RecycleOutcome(Removed: true));

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(link, IsDirectory: true, Bytes: 0)],
            ExploreRemovalMode.RecycleBin,
            _policy,
            bin);

        Assert.Equal(link, Assert.Single(bin.Paths));
        Assert.Single(report.Removed);
        Assert.True(LongPath.FileExists(store));
    }

    /// <summary>
    /// The policy is asked again inside the remover rather than trusted from the caller, so a shell
    /// that never asked cannot get past it. Driven here by handing the remover a refused path
    /// directly, which is what such a shell would do.
    /// </summary>
    [Fact]
    public async Task TheRemoverRefusesEvenWhenNothingAskedItFirst()
    {
        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(_environment.UserProfile, IsDirectory: true, Bytes: 1)],
            ExploreRemovalMode.Permanent,
            _policy);

        Assert.Empty(report.Removed);
        Assert.True(LongPath.DirectoryExists(_environment.UserProfile));
    }

    /// <summary>§5.6: what should have survived is asserted, not assumed.</summary>
    [Fact]
    public async Task EverythingBesideTheRemovedItemIsAssertedToHaveSurvived()
    {
        var target = _temp.CreateFile(8, "profile", "Downloads", "target.bin");
        _temp.CreateFile(8, "profile", "Downloads", "keep-one.bin");
        _temp.CreateFile(8, "profile", "Downloads", "keep-two.bin");

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(target, IsDirectory: false, Bytes: 8)],
            ExploreRemovalMode.RecycleBin,
            _policy,
            new FakeRecycleBin());

        Assert.True(report.Verification.Passed);
        Assert.Contains(
            report.Verification.Checks,
            c => c.Detail.Contains("All 2 other item(s) are still there.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The assertion above with its teeth shown. An over-broad removal takes the siblings with it
    /// and passes every check that its own target went away — which is why asserting the target is
    /// gone is only half a test.
    /// </summary>
    [Fact]
    public async Task AnOverBroadRemovalFailsVerification()
    {
        var target = _temp.CreateFile(8, "profile", "Downloads", "target.bin");
        _temp.CreateFile(8, "profile", "Downloads", "keep-one.bin");

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(target, IsDirectory: false, Bytes: 8)],
            ExploreRemovalMode.RecycleBin,
            _policy,
            FakeRecycleBin.TakingTheParentToo());

        Assert.False(LongPath.FileExists(target));
        Assert.False(report.Verification.Passed);
        Assert.Contains(report.Verification.Failures, c => c.Detail.StartsWith("MISSING", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same assertion against the narrower failure, which is the one the comparison of two
    /// listings actually exists for. When a removal takes the whole folder the check fails because
    /// there is nothing left to list; when it takes one neighbour and leaves the folder standing,
    /// only comparing what was there before with what is there now finds it.
    /// </summary>
    [Fact]
    public async Task ARemovalThatTakesANeighbourFailsVerification()
    {
        var target = _temp.CreateFile(8, "profile", "Downloads", "target.bin");
        _temp.CreateFile(8, "profile", "Downloads", "keep-one.bin");
        _temp.CreateFile(8, "profile", "Downloads", "keep-two.bin");

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(target, IsDirectory: false, Bytes: 8)],
            ExploreRemovalMode.RecycleBin,
            _policy,
            FakeRecycleBin.TakingAlso("keep-one.bin"));

        Assert.True(LongPath.DirectoryExists(Path.GetDirectoryName(target)!));
        Assert.True(LongPath.FileExists(Path.Combine(Path.GetDirectoryName(target)!, "keep-two.bin")));

        Assert.False(report.Verification.Passed);
        Assert.Contains(
            report.Verification.Failures,
            c => c.Detail.Contains("'keep-one.bin'", StringComparison.Ordinal));

        // The user is told, not merely a report field. A negative assertion nobody reads is not one.
        Assert.Contains("did not pass", report.Summary, StringComparison.Ordinal);
        Assert.Contains("Look at the folder", report.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A same-named neighbour in a different folder is not excused.
    ///
    /// <para>The removed set is keyed by whole path rather than by leaf name. Pooling the names
    /// meant removing <c>one\junk</c> also excused <c>two\junk</c> going missing — which is exactly
    /// the over-broad removal §5.6 exists to catch, hidden by the check meant to catch it.</para>
    /// </summary>
    [Fact]
    public async Task ANeighbourSharingItsNameWithARemovedItemIsStillAsserted()
    {
        var target = _temp.CreateFile(8, "profile", "one", "junk.bin");
        _temp.CreateFile(8, "profile", "two", "junk.bin");
        var second = _temp.CreateFile(8, "profile", "two", "other.bin");

        var report = await ExploreRemover.RemoveAsync(
            [
                new ExploreItem(target, IsDirectory: false, Bytes: 8),
                new ExploreItem(second, IsDirectory: false, Bytes: 8),
            ],
            ExploreRemovalMode.RecycleBin,
            _policy,
            FakeRecycleBin.TakingAlso("junk.bin"));

        Assert.False(report.Verification.Passed);
        Assert.Contains(
            report.Verification.Failures,
            c => c.Detail.Contains("'junk.bin'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A folder that would not list its contents leaves §5.6 with nothing to compare against, and
    /// that is recorded as a failure rather than as a pass.
    ///
    /// <para>A non-assertion filed as evidence is the one thing that undoes §5.6, and it would have
    /// been invisible: a passing report says nothing at all, so the sentence explaining that the
    /// folder was never read would have reached nobody.</para>
    /// </summary>
    [Fact]
    public async Task AFolderThatWillNotListItsContentsIsNotEvidence()
    {
        var target = _temp.CreateFile(8, "profile", "Downloads", "big.bin");
        var parent = Path.GetDirectoryName(target)!;

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(target, IsDirectory: false, Bytes: 8)],
            ExploreRemovalMode.RecycleBin,
            _policy,
            new FakeRecycleBin(),
            new UnlistableFileSystem(WindowsFileSystem.Default, parent));

        Assert.Single(report.Removed);
        Assert.False(report.Verification.Passed);
        Assert.Contains(
            report.Verification.Failures,
            c => c.Detail.StartsWith("NOT ESTABLISHED", StringComparison.Ordinal));
        Assert.Contains("did not pass", report.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal from the shell is reported rather than escalated. Falling back to an outright
    /// delete would give the user the irreversible removal they did not ask for.
    /// </summary>
    [Fact]
    public async Task AShellRefusalLeavesTheItemAloneAndSaysSo()
    {
        var file = _temp.CreateFile(8, "profile", "Downloads", "big.bin");

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(file, IsDirectory: false, Bytes: 8)],
            ExploreRemovalMode.RecycleBin,
            _policy,
            FakeRecycleBin.Refusing("Windows would not move this to the Recycle Bin."));

        Assert.Empty(report.Removed);
        Assert.True(LongPath.FileExists(file));
        Assert.Contains("would not move", Assert.Single(report.Refused).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// §5.2 through the remover, not only through the policy. The whole point of asking again here
    /// is that a tool's configuration is refused whichever route reaches it.
    /// </summary>
    [Fact]
    public async Task AnUnrecognisedChildOfAToolRootIsRefusedByTheRemoverToo()
    {
        var gradle = Path.Combine(_environment.UserProfile, ".gradle");
        var properties = _temp.CreateFile(8, "profile", ".gradle", "gradle.properties");

        var policy = new ExploreActionPolicy(
            [],
            [ToolRoot.Of(gradle, "Gradle's own folder.", GradleCacheProvider.DisposableChildren)]);

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(properties, IsDirectory: false, Bytes: 8)],
            ExploreRemovalMode.Permanent,
            policy);

        Assert.Empty(report.Removed);
        Assert.True(LongPath.FileExists(properties));
    }

    /// <summary>
    /// §5.6 for a folder holding a tool's root, picked beside an ordinary folder. The folder holding the
    /// root stays, with the root and its settings in it, the ordinary folder goes, and the check on what
    /// stood beside them passes.
    /// </summary>
    [Fact]
    public async Task AFolderHoldingAToolRootStaysOnTheDiskWithTheRootInIt()
    {
        var root = _temp.CreateDirectory("profile", "AppData", "Local", "Vendor", "Tool");
        var settings = _temp.CreateFile(8, "profile", "AppData", "Local", "Vendor", "Tool", "settings.json");
        var vendor = Path.GetDirectoryName(root)!;
        var ordinary = _temp.CreateDirectory("profile", "AppData", "Local", "Ordinary");
        _temp.CreateFile(32, "profile", "AppData", "Local", "Ordinary", "big.bin");

        var policy = new ExploreActionPolicy(
            [],
            [new ToolRoot(root, "A vendor tool's own folder.", static _ => false)]);

        var report = await ExploreRemover.RemoveAsync(
            [
                new ExploreItem(vendor, IsDirectory: true, Bytes: 8),
                new ExploreItem(ordinary, IsDirectory: true, Bytes: 32),
            ],
            ExploreRemovalMode.Permanent,
            policy);

        Assert.True(LongPath.FileExists(settings));
        Assert.Equal(vendor, Assert.Single(report.Refused).Path);
        Assert.Contains(root, report.Refused[0].Message, StringComparison.Ordinal);
        Assert.Equal(ordinary, Assert.Single(report.Removed).Path);
        Assert.False(LongPath.DirectoryExists(ordinary));
        Assert.True(report.Verification.Passed, report.Summary);
    }
}
