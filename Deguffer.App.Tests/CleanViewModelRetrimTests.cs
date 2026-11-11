using Deguffer.Core.Execution;
using Deguffer.Core.Retrim;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// How the Storage page wires the retrim offer: made after a clean from the volumes it freed space on,
/// accepted only by the user, run here as an administrator and through a restart as one otherwise.
/// Which volumes qualify is <see cref="RetrimOffer"/>'s, and is proved in Core.
/// </summary>
public class CleanViewModelRetrimTests
{
    private const long GiB = 1L << 30;

    private const string VolumeName = @"\\?\Volume{33333333-3333-3333-3333-333333333333}\";

    private const byte Nvme = 0x11;
    private const byte Sata = 0x0B;

    /// <summary>
    /// A page over one named volume on <paramref name="bus"/>, with a row whose clean grows the
    /// volume's free space from 10 GiB by <paramref name="freed"/>.
    /// </summary>
    private static StoragePage Cleaned(bool isElevated, long freed = 4 * GiB, byte bus = Nvme, bool seekPenalty = false)
    {
        var cache = new FakeCleanupProvider("cache");
        var page = new StoragePage([cache], isElevated, freeBytes: 10 * GiB, volumeName: VolumeName);
        page.Storage.Volume(page.VolumeRoot, 0).Disk(0, bus, seekPenalty).Thin(0, false);
        cache.Steps = [page.Cache("taken", 1024)];
        cache.AfterCleaning = () => page.Volumes.FreeNow(page.VolumeRoot, (10 * GiB) + freed);
        page.Scan();
        page.Clean();
        return page;
    }

    private static void Accept(StoragePage page) =>
        UiThread.Run(() => page.ViewModel.Retrim.RetrimCommand.ExecuteAsync(null));

    [Fact]
    public void ACleanThatFreesALotOnSolidStateOffersARetrimAndRunsNothing()
    {
        using var page = Cleaned(isElevated: true);
        var retrim = page.ViewModel.Retrim;

        Assert.True(retrim.IsOffered);
        Assert.True(retrim.IsShown);
        Assert.Contains("The clean freed 4.0 GB on", retrim.OfferText);
        Assert.Equal($"Retrim {page.VolumeRoot}", retrim.ActionLabel);
        Assert.Empty(page.Retrim.Asked);
    }

    /// <summary>
    /// Accepted as an administrator, Windows is asked by the volume's name, its answer is on the card,
    /// and the retrim is recorded as running while Windows works, so the window waits for it.
    /// </summary>
    [Fact]
    public void AnAcceptedOfferRetrimsByTheVolumesNameAndReportsWindowsAnswer()
    {
        using var page = Cleaned(isElevated: true);
        IReadOnlyList<RunningAction> whileRetrimming = [];
        page.Retrim.WhileRetrimming = () => whileRetrimming = page.Running.Current;

        Accept(page);

        Assert.Equal([VolumeName], page.Retrim.Asked);
        Assert.Equal([RunningAction.Retrim], whileRetrimming);
        Assert.True(page.Running.MayEndProcess);
        Assert.Equal([$"Windows retrimmed {page.VolumeRoot}."], page.ViewModel.Retrim.Results);
        Assert.False(page.ViewModel.Retrim.IsOffered);
        Assert.False(page.ViewModel.Retrim.IsRetrimming);
        Assert.Empty(page.Relaunches);
    }

    /// <summary>
    /// Not an administrator: the offer says so, and accepting it restarts Deguffer as one with the
    /// volume on the command line, and asks Windows nothing here.
    /// </summary>
    [Fact]
    public void UnelevatedTheOfferRestartsAsAdministratorWithTheVolume()
    {
        using var page = Cleaned(isElevated: false);
        var replaced = false;
        page.ViewModel.Retrim.ReplacedByElevatedInstance += (_, _) => replaced = true;

        Assert.Equal("Restart as administrator and retrim", page.ViewModel.Retrim.ActionLabel);
        Assert.Contains("restarts as administrator", page.ViewModel.Retrim.OfferText);

        Accept(page);

        var request = Assert.IsType<RetrimRequest>(Assert.Single(page.Relaunches));
        Assert.Equal([new RetrimTarget(page.VolumeRoot, VolumeName)], request.Targets);
        Assert.True(replaced);
        Assert.Empty(page.Retrim.Asked);
    }

    [Fact]
    public void ADeclinedRestartSaysNothingWasRetrimmedAndKeepsTheOffer()
    {
        using var page = Cleaned(isElevated: false);
        page.RelaunchAccepted = false;

        Accept(page);

        Assert.Equal([RetrimReport.NotRestarted], page.ViewModel.Retrim.Results);
        Assert.True(page.ViewModel.Retrim.IsOffered);
        Assert.Empty(page.Retrim.Asked);
    }

    /// <summary>The unrecognised case: a spinning disk is offered nothing, however much was freed.</summary>
    [Fact]
    public void ASpinningDiskIsOfferedNothing()
    {
        using var page = Cleaned(isElevated: true, freed: 50 * GiB, bus: Sata, seekPenalty: true);

        Assert.False(page.ViewModel.Retrim.IsOffered);
        Assert.False(page.ViewModel.Retrim.IsShown);
    }

    [Fact]
    public void ACleanThatFreesLittleOffersNothing()
    {
        using var page = Cleaned(isElevated: true, freed: GiB / 2);

        Assert.False(page.ViewModel.Retrim.IsOffered);
    }

    /// <summary>
    /// A new clean takes down the offer and the results of the one before it. A declined restart leaves
    /// both on screen, and the second clean frees nothing more, so nothing it finds puts them back.
    /// </summary>
    [Fact]
    public void ANewCleanTakesDownTheLastOnesOfferAndResults()
    {
        using var page = Cleaned(isElevated: false);
        page.RelaunchAccepted = false;
        Accept(page);
        Assert.True(page.ViewModel.Retrim.IsOffered);
        Assert.NotEmpty(page.ViewModel.Retrim.Results);

        page.Scan();
        page.Clean();

        Assert.False(page.ViewModel.Retrim.IsOffered);
        Assert.Empty(page.ViewModel.Retrim.Results);
        Assert.False(page.ViewModel.Retrim.IsShown);
    }

    /// <summary>
    /// The restarted instance retrims what it was asked to with no offer first, judging the volume
    /// again, and a volume no longer offered is reported and not retrimmed.
    /// </summary>
    [Fact]
    public void ARequestedRetrimRunsWithoutAnOfferAndJudgesEachVolumeAgain()
    {
        using var page = new StoragePage([new FakeCleanupProvider("cache")], isElevated: true, freeBytes: 10 * GiB, volumeName: VolumeName);
        page.Storage.Volume(page.VolumeRoot, 0).Disk(0, Nvme, seekPenalty: false);

        RetrimRequest request = new([new(page.VolumeRoot, VolumeName), new(@"Z:\", @"\\?\Volume{99999999-9999-9999-9999-999999999999}\")]);

        UiThread.Run(() => page.ViewModel.Retrim.RunRequestedAsync(request, CancellationToken.None));

        Assert.Equal([VolumeName], page.Retrim.Asked);
        Assert.Equal(
            [$"Windows retrimmed {page.VolumeRoot}.", "Z: is no longer a drive a retrim is offered on, so it was not retrimmed."],
            page.ViewModel.Retrim.Results);
    }
}
