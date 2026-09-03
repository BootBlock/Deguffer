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

        _policy = ExploreActionPolicy.For(_system, _environment, []);
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
}
