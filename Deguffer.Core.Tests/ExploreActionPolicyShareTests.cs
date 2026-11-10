using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.1 on a share: Explore refuses to remove what is on a share or a network drive, or what a local
/// link leads to on one, because a share can be this computer's own disk under a second name, and no
/// rule is written in that form. <c>\\localhost\C$\Windows</c> is <c>C:\Windows</c>.
///
/// <para>Nothing on a share is opened to find out, so each refusal here is also asserted to have
/// asked Windows nothing: opening is a conversation with another machine.</para>
/// </summary>
public sealed class ExploreActionPolicyShareTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeSystemDirectories _system;
    private readonly FakeUserEnvironment _environment;

    public ExploreActionPolicyShareTests()
    {
        _system = new FakeSystemDirectories(_temp.Path);
        _environment = new FakeUserEnvironment(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A UNC path, in each name this computer answers to and in the extended-length form (§6.3), and
    /// on another server, which Deguffer cannot tell from this one. A folder directly on the share is
    /// asked about as surely as one deeper in it.
    /// </summary>
    [Theory]
    [InlineData(@"\\localhost\C$\Windows")]
    [InlineData(@"\\localhost\C$\Windows\System32")]
    [InlineData(@"\\127.0.0.1\C$\Users\another-account\Documents")]
    [InlineData(@"\\0--1.ipv6-literal.net\ADMIN$\System32")]
    [InlineData(@"\\?\UNC\localhost\C$\Windows\System32")]
    [InlineData(@"\\fileserver.test\photos\2019\big.bin")]
    public void AnItemOnAShareIsRefusedWithoutOpeningIt(string item)
    {
        var recording = new RecordingFileSystem(WindowsFileSystem.Default);
        var policy = Policy(new FakeVolumeInventory(), recording);
        var asked = recording.Resolved.Count;

        var verdict = policy.MayRemove(item);

        Assert.False(verdict.IsAllowed);
        Assert.Contains("on a network share or a network drive", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("this computer's own disk", verdict.Reason, StringComparison.Ordinal);
        Assert.Equal(asked, recording.Resolved.Count);
    }

    /// <summary>
    /// A link on the share is not followed, even where Windows would say it leads to an ordinary
    /// folder on a local disk: the item is refused without asking, so what the server says a link
    /// leads to can neither allow it nor cost a conversation with that server.
    /// </summary>
    [Fact]
    public void AnItemThroughALinkOnAShareIsRefusedWithoutFollowingIt()
    {
        var ordinary = _temp.CreateDirectory("profile", "Downloads");
        var link = @"\\fileserver.test\photos\linked";
        var recording = new RecordingFileSystem(
            new RedirectingFileSystem(WindowsFileSystem.Default, link, LongPath.Extended(ordinary)));
        var policy = Policy(new FakeVolumeInventory(), recording);
        var asked = recording.Resolved.Count;

        var verdict = policy.MayRemove(Path.Join(link, "big.bin"));

        Assert.False(verdict.IsAllowed);
        Assert.Contains($"'{link}' is on a network share or a network drive", verdict.Reason, StringComparison.Ordinal);
        Assert.Equal(asked, recording.Resolved.Count);
    }

    /// <summary>
    /// A drive letter mapped to a share, which the volume list reports as a network drive.
    /// <c>Z:\Windows</c> on a letter mapped to <c>\\localhost\C$</c> is <c>C:\Windows</c>.
    /// </summary>
    [Fact]
    public void AnItemOnAMappedDriveIsRefusedWithoutOpeningIt()
    {
        var recording = new RecordingFileSystem(WindowsFileSystem.Default);
        var volumes = new FakeVolumeInventory().With(@"C:\").With(@"Z:\", kind: DriveType.Network);
        var policy = Policy(volumes, recording);
        var asked = recording.Resolved.Count;

        var verdict = policy.MayRemove(@"Z:\Windows\System32");

        Assert.False(verdict.IsAllowed);
        Assert.Contains(@"'Z:\Windows' is on a network share or a network drive", verdict.Reason, StringComparison.Ordinal);
        Assert.Equal(asked, recording.Resolved.Count);
    }

    /// <summary>
    /// A letter mapped after the volume list was read is on no volume the list holds, so it is opened
    /// as a local one, and Windows names the share it leads to.
    /// </summary>
    [Fact]
    public void AnItemOnADriveMappedSinceTheListWasReadIsRefused()
    {
        var policy = Policy(
            new FakeVolumeInventory().With(@"C:\"),
            new RedirectingFileSystem(WindowsFileSystem.Default, @"Z:\Windows", @"\\?\UNC\localhost\C$\Windows"));

        var verdict = policy.MayRemove(@"Z:\Windows\System32");

        Assert.False(verdict.IsAllowed);
        Assert.Contains(@"leads to the network share '\\localhost\C$\Windows'", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A local link that leads to a share is refused for the same reason as an item on the share.
    /// </summary>
    [Fact]
    public void AnItemThroughALinkToAShareIsRefused()
    {
        var folder = _temp.CreateDirectory("profile", "Linked");
        var policy = Policy(
            new FakeVolumeInventory(),
            new RedirectingFileSystem(WindowsFileSystem.Default, folder, @"\\?\UNC\localhost\C$\Windows"));

        var verdict = policy.MayRemove(Path.Combine(folder, "System32"));

        Assert.False(verdict.IsAllowed);
        Assert.Contains(@"\\localhost\C$\Windows", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A drive that keeps its files in the cloud is not opened either, and holds no link, so what is
    /// on it is still judged by its path rather than refused with the shares.
    /// </summary>
    [Fact]
    public void AnItemOnACloudDriveIsJudgedByItsPathWithoutOpeningIt()
    {
        var recording = new RecordingFileSystem(WindowsFileSystem.Default);
        var volumes = new FakeVolumeInventory().With(@"C:\").With(@"V:\", features: (VolumeFeatures)0x0000_0106);
        var policy = Policy(volumes, recording);
        var asked = recording.Resolved.Count;

        Assert.True(policy.MayRemove(@"V:\Photos\2019\big.bin").IsAllowed);
        Assert.Equal(asked, recording.Resolved.Count);
    }

    /// <summary>
    /// §5.6: a removal asked of an item on a share deletes nothing, sends nothing to the Recycle Bin,
    /// and hands Windows no path at all, permanent mode included.
    ///
    /// <para>On a server no name resolves to, never on <c>\\localhost</c>: the removal hands what the
    /// policy allows to the real filesystem, so a regression here would otherwise delete this
    /// machine's own files through its administrative share.</para>
    /// </summary>
    [Fact]
    public async Task ARemovalOnAShareTouchesNothing()
    {
        var recording = new RecordingFileSystem(WindowsFileSystem.Default);
        var bin = new FakeRecycleBin();
        var item = @"\\fileserver.test\photos\2019";
        var policy = Policy(new FakeVolumeInventory(), recording);
        var handed = recording.Paths.Count;

        var report = await ExploreRemover.RemoveAsync(
            [new ExploreItem(item, IsDirectory: true, Bytes: 64, LowerBound: false)],
            ExploreRemovalMode.Permanent,
            policy,
            bin,
            recording);

        Assert.Empty(report.Removed);
        Assert.Equal(item, Assert.Single(report.Refused).Path);
        Assert.Empty(bin.Paths);
        Assert.Equal(handed, recording.Paths.Count);
    }

    /// <summary>
    /// Explore scans a share, and a mapped drive, and refuses to remove anything on either, the top
    /// of the share included. Nothing is asked of Windows to decide it, not even where the path's
    /// volume is mounted: that question is a conversation with the server, and the policy is asked
    /// about every one of the largest files on a share and about a row on the UI thread.
    ///
    /// <para>The §5.6 negative beside it: an ordinary folder on a local drive in the same inventory
    /// is still allowed, so the refusal is the share's and not a refusal of everything.</para>
    /// </summary>
    [Theory]
    [InlineData(@"\\fileserver.test\photos", @"'\\fileserver.test\photos' is on a network share")]
    [InlineData(@"\\fileserver.test\photos\", @"'\\fileserver.test\photos' is on a network share")]
    [InlineData(@"\\fileserver.test\photos\2019", @"'\\fileserver.test\photos' is on a network share")]
    [InlineData(@"Z:\", @"'Z:\' is on a network share")]
    [InlineData(@"Z:\Photos", @"'Z:\' is on a network share")]
    public void AShareOrAMappedDriveIsRefusedWithoutAskingWhereItIsMounted(string item, string reason)
    {
        var ordinary = _temp.CreateDirectory("profile", "Downloads", "old");
        var recording = new RecordingFileSystem(WindowsFileSystem.Default);
        var volumes = new FakeVolumeInventory()
            .With(Path.GetPathRoot(_temp.Path)!)
            .With(@"Z:\", kind: DriveType.Network, readiness: VolumeReadiness.NotAsked);
        var policy = Policy(volumes, recording);
        var asked = recording.Resolved.Count;
        volumes.MountPointQueries.Clear();

        var verdict = policy.MayRemove(item);

        Assert.False(verdict.IsAllowed);
        Assert.Contains(reason, verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("this computer's own disk", verdict.Reason, StringComparison.Ordinal);
        Assert.Equal(asked, recording.Resolved.Count);
        Assert.Empty(volumes.MountPointQueries);

        Assert.True(policy.MayRemove(ordinary).IsAllowed, policy.MayRemove(ordinary).Reason);
    }

    private ExploreActionPolicy Policy(IVolumeInventory volumes, IFileSystem fileSystem) =>
        new(ProtectedRegions.For(_system, _environment), [], volumes, fileSystem);
}
