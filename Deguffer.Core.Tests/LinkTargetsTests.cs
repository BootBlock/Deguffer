using Deguffer.Core.Exploring.Links;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether what a link points at is gone, cannot be reached, or is there. Gone is the one answer that
/// is offered, so every way of not knowing has to land on unreachable, and a refusal is never absence.
/// All through the path probe and volume fakes: no drive here is real.
/// </summary>
public sealed class LinkTargetsTests
{
    private readonly FakePathProbe _probe = new();
    private readonly FakeVolumeInventory _volumes = new FakeVolumeInventory().With(@"C:\");

    private LinkTargets Targets() => new(_probe, _volumes);

    [Fact]
    public void ATargetOnAFixedDiskThatAnswersAbsentIsGone() =>
        Assert.Equal(LinkTargetState.Gone, Targets().StateOf(@"C:\Program Files\Removed\app.exe"));

    [Fact]
    public void ATargetThatIsThereIsPresent()
    {
        _probe.File(@"C:\Program Files\Kept\app.exe").Directory(@"C:\Program Files\Kept");

        var targets = Targets();

        Assert.Equal(LinkTargetState.Present, targets.StateOf(@"C:\Program Files\Kept\app.exe"));
        Assert.Equal(LinkTargetState.Present, targets.StateOf(@"C:\Program Files\Kept"));
    }

    /// <summary>A refusal is never absence: a target Windows would not describe may be there.</summary>
    [Fact]
    public void ATargetWindowsWillNotDescribeIsUnreachable()
    {
        _probe.Refused(@"C:\Locked\app.exe");

        Assert.Equal(LinkTargetState.Unreachable, Targets().StateOf(@"C:\Locked\app.exe"));
    }

    /// <summary>
    /// A drive nothing is mounted at reads absent for every path on it, which says nothing about the
    /// path. It is not even asked: Windows answers nothing about a letter it does not have.
    /// </summary>
    [Fact]
    public void ATargetOnADriveThatIsNotThereIsUnreachableAndNeverProbed()
    {
        Assert.Equal(LinkTargetState.Unreachable, Targets().StateOf(@"E:\Games\game.exe"));
        Assert.Empty(_probe.EntryQueries);
    }

    /// <summary>
    /// A removable drive, a network drive, a disc and a drive with no media in it each read absent for
    /// a path they would hold on another day, so none is gone, however plainly it answers.
    /// </summary>
    [Theory]
    [InlineData(DriveType.Removable, VolumeReadiness.Ready)]
    [InlineData(DriveType.Network, VolumeReadiness.Ready)]
    [InlineData(DriveType.CDRom, VolumeReadiness.Ready)]
    [InlineData(DriveType.Fixed, VolumeReadiness.NoMedia)]
    [InlineData(DriveType.Fixed, VolumeReadiness.Refused)]
    public void ATargetOnAnythingButAReadyFixedDiskIsUnreachable(DriveType kind, VolumeReadiness readiness)
    {
        _volumes.With(@"F:\", kind, readiness);

        Assert.Equal(LinkTargetState.Unreachable, Targets().StateOf(@"F:\Photos\shoot.cr3"));
        Assert.Empty(_probe.EntryQueries);
    }

    /// <summary>
    /// Windows reads a share that does not answer, or a drive that is not ready, as "nothing is there"
    /// for every path through it. So a target behind a folder link on a fixed disk that is ready reads
    /// absent while the NAS the link leads to is switched off, and is not gone. A folder on the way that
    /// Windows will not describe hides the same question.
    /// </summary>
    [Fact]
    public void AnAbsentTargetBehindALinkOrARefusedFolderIsUnreachable()
    {
        _probe.Directory(@"C:\Users", isLink: false)
            .Directory(@"C:\Users\testuser", isLink: false)
            .Directory(@"C:\Users\testuser\Music", isLink: true)
            .Refused(@"C:\Users\testuser\Locked");

        var targets = Targets();

        Assert.Equal(LinkTargetState.Unreachable, targets.StateOf(@"C:\Users\testuser\Music\Player\player.exe"));
        Assert.Equal(LinkTargetState.Unreachable, targets.StateOf(@"C:\Users\testuser\Locked\Tool\tool.exe"));
        Assert.Equal(LinkTargetState.Gone, targets.StateOf(@"C:\Users\testuser\Removed\app.exe"));
    }

    /// <summary>A share can be another computer's disk under the same name tomorrow, and it is never asked.</summary>
    [Theory]
    [InlineData(@"\\server\share\tools\tool.exe")]
    [InlineData(@"\\?\UNC\server\share\tools\tool.exe")]
    public void ATargetOnAShareIsUnreachableAndNeverProbed(string target)
    {
        Assert.Equal(LinkTargetState.Unreachable, Targets().StateOf(target));
        Assert.Empty(_probe.EntryQueries);
    }

    /// <summary>
    /// A folder a volume is mounted at points at the volume by name. A volume that is not attached is a
    /// disk unplugged, never one that is gone.
    /// </summary>
    [Fact]
    public void AVolumeNamedByItsGuidIsPresentOrUnreachableAndNeverGone()
    {
        const string Attached = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\";
        const string Detached = @"\\?\Volume{99999999-8888-7777-6666-555555555555}\";
        _probe.Directory(Attached).Disconnected(Detached);

        var targets = Targets();

        Assert.Equal(LinkTargetState.Present, targets.StateOf(Attached));
        Assert.Equal(LinkTargetState.Unreachable, targets.StateOf(Detached));
    }

    [Theory]
    [InlineData(@"relative\app.exe")]
    [InlineData(@"\rooted\without\a\drive.exe")]
    [InlineData(@"C:drive-relative.exe")]
    public void ATargetThatIsNotAFullPathIsUnreachable(string target) =>
        Assert.Equal(LinkTargetState.Unreachable, Targets().StateOf(target));

    /// <summary>G4: one operation asks about a target once, however many links point at it.</summary>
    [Fact]
    public void ATargetIsAskedAboutOncePerInstance()
    {
        var targets = Targets();

        targets.StateOf(@"C:\Gone\app.exe");
        targets.StateOf(@"C:\Gone\app.exe");

        Assert.Equal(1, _probe.EntryQueries[@"C:\Gone\app.exe"]);
    }

    /// <summary>The answer a removal stands on is asked of the machine again, past what was remembered.</summary>
    [Fact]
    public void TheAnswerNowIsAskedAgainAndSeesATargetThatCameBack()
    {
        var targets = Targets();
        Assert.Equal(LinkTargetState.Gone, targets.StateOf(@"C:\Back\app.exe"));

        _probe.File(@"C:\Back\app.exe");

        Assert.Equal(LinkTargetState.Gone, targets.StateOf(@"C:\Back\app.exe"));
        Assert.Equal(LinkTargetState.Present, targets.StateNow(@"C:\Back\app.exe"));
        Assert.Equal(LinkTargetState.Present, targets.StateOf(@"C:\Back\app.exe"));
    }
}
