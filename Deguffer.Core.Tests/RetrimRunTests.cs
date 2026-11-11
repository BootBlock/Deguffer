using Deguffer.Core.Retrim;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning.Media;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The run that asks Windows to retrim, against a retrim that records what it was asked and answers
/// as told. Nothing here retrims the machine.
/// </summary>
public sealed class RetrimRunTests
{
    private const string C = @"C:\";
    private const string D = @"D:\";
    private const string NameOfC = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\";
    private const string NameOfD = @"\\?\Volume{22222222-2222-2222-2222-222222222222}\";

    private static readonly IReadOnlyList<LocalVolume> Mounted =
    [
        new(C, DriveType.Fixed, VolumeReadiness.Ready, VolumeName: NameOfC),
        new(D, DriveType.Fixed, VolumeReadiness.Ready, VolumeName: NameOfD),
    ];

    private sealed class Collected : IProgress<RetrimResult>
    {
        public List<RetrimResult> Results { get; } = [];

        public void Report(RetrimResult value) => Results.Add(value);
    }

    private static async Task<(FakeVolumeRetrim Retrim, List<RetrimResult> Results)> Run(
        IReadOnlyList<RetrimTarget> targets,
        Func<LocalVolume, VolumeMedia> media,
        RetrimAnswer? answer = null,
        CancellationToken ct = default)
    {
        var retrim = new FakeVolumeRetrim { Answer = answer ?? new RetrimAnswer(RetrimOutcome.Retrimmed) };
        var collected = new Collected();

        await RetrimRun.ExecuteAsync(targets, Mounted, media, retrim, collected, ct);

        return (retrim, collected.Results);
    }

    /// <summary>Windows is asked by the volume's name, not its letter, and each result is reported in order.</summary>
    [Fact]
    public async Task EachVolumeIsRetrimmedByItsNameInOrder()
    {
        var (retrim, results) = await Run([new(D, NameOfD), new(C, NameOfC)], _ => new VolumeMedia(StorageMedia.Nvme, [0]));

        Assert.Equal([NameOfD, NameOfC], retrim.Asked);
        Assert.Equal([D, C], results.Select(r => r.Root));
        Assert.All(results, r => Assert.Equal(RetrimOutcome.Retrimmed, r.Answer.Outcome));
    }

    /// <summary>
    /// A request from the command line is judged again: a volume no clean could have offered a retrim
    /// on, here a spinning disk, is never retrimmed, and the one beside it still is.
    /// </summary>
    [Fact]
    public async Task AVolumeThatNoLongerQualifiesIsNotRetrimmed()
    {
        var (retrim, results) = await Run(
            [new(C, NameOfC), new(D, NameOfD)],
            volume => volume.RootPath == C
                ? new VolumeMedia(StorageMedia.Rotational, [0], ThinlyProvisioned: false)
                : new VolumeMedia(StorageMedia.SolidState, [1]));

        Assert.Equal([NameOfD], retrim.Asked);
        Assert.Equal(RetrimOutcome.NotOffered, results[0].Answer.Outcome);
        Assert.Equal(RetrimOutcome.Retrimmed, results[1].Answer.Outcome);
    }

    [Fact]
    public async Task AVolumeThatIsNotMountedIsNotRetrimmed()
    {
        var (retrim, results) = await Run(
            [new(@"Z:\", @"\\?\Volume{99999999-9999-9999-9999-999999999999}\")],
            _ => new VolumeMedia(StorageMedia.Nvme, [0]));

        Assert.Empty(retrim.Asked);
        Assert.Equal(RetrimOutcome.NotOffered, Assert.Single(results).Answer.Outcome);
    }

    /// <summary>
    /// The letter the user accepted now stands for another volume, here after a restart as administrator.
    /// The volume at that letter is never retrimmed in its place: the one accepted is found by its name,
    /// wherever it is mounted now, and the result still names the letter the offer showed.
    /// </summary>
    [Fact]
    public async Task AMovedLetterNeverRetrimsTheVolumeNowAtIt()
    {
        const string Accepted = @"\\?\Volume{44444444-4444-4444-4444-444444444444}\";

        var (retrim, results) = await Run([new(D, Accepted)], _ => new VolumeMedia(StorageMedia.Nvme, [0]));

        Assert.DoesNotContain(NameOfD, retrim.Asked);
        Assert.Empty(retrim.Asked);
        Assert.Equal(new RetrimResult(D, new RetrimAnswer(RetrimOutcome.NotOffered)), Assert.Single(results));
    }

    [Fact]
    public async Task TheAcceptedVolumeIsRetrimmedWhereverItIsMountedNow()
    {
        var (retrim, results) = await Run([new(@"F:\", NameOfD)], _ => new VolumeMedia(StorageMedia.Nvme, [0]));

        Assert.Equal([NameOfD], retrim.Asked);
        Assert.Equal(@"F:\", Assert.Single(results).Root);
    }

    [Fact]
    public async Task WindowsOwnAnswerIsReported()
    {
        var (_, results) = await Run([new(C, NameOfC)], _ => new VolumeMedia(StorageMedia.Nvme, [0]), new RetrimAnswer(RetrimOutcome.Failed, 3));

        Assert.Equal(new RetrimAnswer(RetrimOutcome.Failed, 3), Assert.Single(results).Answer);
    }

    [Fact]
    public async Task ACancelledRunAsksWindowsNothingMore()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var retrim = new FakeVolumeRetrim();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RetrimRun.ExecuteAsync([new(C, NameOfC)], Mounted, _ => new VolumeMedia(StorageMedia.Nvme, [0]), retrim, new Collected(), cancelled.Token));

        Assert.Empty(retrim.Asked);
    }
}
