using Deguffer.Core.Exploring;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Explore page wires the box a location is typed into: the page points at what was typed
/// only once a probe has found a folder there, and stays where it was, saying why, where it has not.
/// What counts as a path and the wording are Core's (<c>TypedFolderTests</c>).
/// </summary>
public sealed class ExploreTypedFolderTests : IDisposable
{
    private const string Share = @"\\server.test\share";

    private readonly ExploreFixture _explore = new();

    public void Dispose() => _explore.Dispose();

    /// <summary>
    /// A share typed and found is the scan's root, with the drive box left as it was, since no drive
    /// in it holds the share. The probe is asked in the extended form (§6.3).
    /// </summary>
    [Fact]
    public async Task AShareThatIsThereIsWhatTheNextScanCovers()
    {
        _explore.Volumes.With(@"C:\");
        _explore.Probe.Directory(@"\\?\UNC\server.test\share");
        var page = _explore.Page();
        var accepted = 0;
        page.Targeting.Typed.Accepted += (_, _) => accepted++;

        page.Targeting.Typed.Text = $"  {Share}  ";
        await page.Targeting.Typed.GoCommand.ExecuteAsync(null);

        Assert.Equal(Share, page.Targeting.Target.Root);
        Assert.Equal(@"C:\", page.Targeting.SelectedDrive?.RootPath);
        Assert.Null(page.Targeting.Typed.Refusal);
        Assert.Equal(1, accepted);
        Assert.Equal(1, _explore.Probe.DirectoryQueries[@"\\?\UNC\server.test\share"]);
        Assert.True(page.ScanCommand.CanExecute(null));
    }

    /// <summary>A share that is not there leaves the page where it was, and the box says why.</summary>
    [Fact]
    public async Task AShareThatIsNotThereLeavesThePageWhereItWas()
    {
        _explore.Volumes.With(@"C:\");
        _explore.Probe.Disconnected(@"\\?\UNC\server.test\share");
        var page = _explore.Page();
        var accepted = 0;
        page.Targeting.Typed.Accepted += (_, _) => accepted++;

        page.Targeting.Typed.Text = Share;
        await page.Targeting.Typed.GoCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\", page.Targeting.Target.Root);
        Assert.Equal(TypedFolder.Refusal(Share, Core.Safety.PathPresence.Absent), page.Targeting.Typed.Refusal);
        Assert.True(page.Targeting.Typed.HasRefusal);
        Assert.Equal(0, accepted);
    }

    /// <summary>A share Windows would not describe is refused in its own words, and is not called absent.</summary>
    [Fact]
    public async Task AShareWindowsWouldNotDescribeIsNotCalledAbsent()
    {
        _explore.Volumes.With(@"C:\");
        _explore.Probe.Refused(@"\\?\UNC\server.test\share");
        var page = _explore.Page();

        page.Targeting.Typed.Text = Share;
        await page.Targeting.Typed.GoCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\", page.Targeting.Target.Root);
        Assert.Equal(TypedFolder.Refusal(Share, Core.Safety.PathPresence.Refused), page.Targeting.Typed.Refusal);
    }

    /// <summary>Text that is not a full path is refused without asking the machine anything.</summary>
    [Fact]
    public async Task TextThatIsNotAPathAsksNothing()
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();

        page.Targeting.Typed.Text = @"server.test\share";
        await page.Targeting.Typed.GoCommand.ExecuteAsync(null);

        Assert.Equal(TypedFolder.NotAPath, page.Targeting.Typed.Refusal);
        Assert.Empty(_explore.Probe.DirectoryQueries);
        Assert.Equal(@"C:\", page.Targeting.Target.Root);
    }

    /// <summary>
    /// The page cannot be pointed elsewhere while a scan runs, so neither can the box, and it comes
    /// back when the scan ends.
    /// </summary>
    [Fact]
    public void TheBoxCannotPointThePageWhileItIsBusy()
    {
        _explore.Volumes.With(@"C:\");
        var page = _explore.Page();
        page.Targeting.Typed.Text = Share;

        Assert.True(page.Targeting.Typed.GoCommand.CanExecute(null));

        page.Targeting.Gate = new ExploreGate(Busy: true, Replaying: false);

        Assert.False(page.Targeting.Typed.GoCommand.CanExecute(null));

        page.Targeting.Gate = new ExploreGate(Busy: false, Replaying: false);

        Assert.True(page.Targeting.Typed.GoCommand.CanExecute(null));
    }

    /// <summary>
    /// A mapped drive chosen in the box withdraws the elevated scan, which would open on a drive the
    /// elevated process cannot see, and a local drive chosen again puts it back. A scan of the mapped
    /// drive that could not list it says that nothing was measured, rather than leaving an empty map
    /// under an empty line, and keeps nothing to compare growth with.
    /// </summary>
    [Fact]
    public void AMappedDriveIsScannedWithoutTheElevatedOffer() => UiThread.Run(async () =>
    {
        _explore.Volumes.With(@"C:\").With(@"Z:\", DriveType.Network, readiness: Core.Safety.VolumeReadiness.NotAsked);
        var page = _explore.Page();

        Assert.True(page.Elevation.CanElevate);

        page.Targeting.SelectedDrive = page.Targeting.Drives.Single(drive => drive.RootPath == @"Z:\");

        Assert.False(page.Elevation.CanElevate);
        Assert.True(page.ScanCommand.CanExecute(null));

        var builder = new ExploreTreeBuilder(@"Z:\");
        builder.MarkListingRefused(ExploreTreeBuilder.RootNode);
        var running = page.ScanCommand.ExecuteAsync(null);

        _explore.Scanner.Current.Finish(ExploreScan.Walked(builder.Build(ExploreChildOrder.BySize), Core.Scanning.FallbackReason.VolumeNotAddressable));
        await running;

        Assert.Equal(@"Z:\", _explore.Scanner.Current.Root);
        Assert.StartsWith(@"Windows would not list 'Z:\', so nothing in it was measured.", page.Status.Sentence, StringComparison.Ordinal);
        Assert.False(page.Elevation.CanElevate);
        Assert.Empty(_explore.History.All());

        page.Targeting.SelectedDrive = page.Targeting.Drives.Single(drive => drive.RootPath == @"C:\");

        Assert.True(page.Elevation.CanElevate);
    });

    /// <summary>A path a later reading moves the drive box under keeps the folder, as a picked one does.</summary>
    [Fact]
    public async Task AFolderTypedOnAMappedDriveMovesTheDriveBoxToIt()
    {
        _explore.Volumes.With(@"C:\").With(@"Z:\", DriveType.Network, readiness: Core.Safety.VolumeReadiness.NotAsked);
        _explore.Probe.Directory(@"\\?\Z:\Photos");
        var page = _explore.Page();

        page.Targeting.Typed.Text = @"Z:\Photos";
        await page.Targeting.Typed.GoCommand.ExecuteAsync(null);

        Assert.Equal(@"Z:\", page.Targeting.SelectedDrive?.RootPath);
        Assert.Equal(@"Z:\Photos", page.Targeting.Target.Root);
    }
}
