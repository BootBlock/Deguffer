using Deguffer.Core.Health;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the places that list volumes say about the drives under one, from what Health last read
/// (§7.6): nothing until a drive is a concern, the worst drive of a spanned volume, and nothing for a
/// drive that has gone or a volume that has moved.
/// </summary>
public sealed class HealthByVolumeTests
{
    private static readonly DriveDescription System = new(new DriveKey("a"), 0, "System SSD");
    private static readonly DriveDescription Data = new(new DriveKey("b"), 1, "Data SSD");

    private readonly HealthByVolume _health = new();

    private static DriveHealth Report(DriveDescription drive, DriveVerdict verdict) =>
        new(drive.Key, drive.Name, verdict, "summary", null, [], [], [], false);

    private static DriveVolumes On(DriveDescription drive, params string[] roots) => new(drive.Key, string.Join("; ", roots), roots);

    [Fact]
    public void NothingIsSaidBeforeHealthHasRead() => Assert.Null(_health.Of(@"C:\"));

    [Theory]
    [InlineData(DriveVerdict.Good)]
    [InlineData(DriveVerdict.Unknown)]
    public void ADriveWithNoConcernAddsNothing(DriveVerdict verdict)
    {
        // Unknown is what raw figures alone give: a drive with no overall judgement of its own.
        _health.Record(On(System, @"C:\"));
        _health.Record(Report(System, verdict));

        Assert.Null(_health.Of(@"C:\"));
    }

    [Fact]
    public void ADriveToWatchIsNamedBesideItsVolume()
    {
        _health.Record(On(System, @"C:\"));
        _health.Record(Report(System, DriveVerdict.Watch));

        var note = _health.Of(@"c:\");

        Assert.NotNull(note);
        Assert.Equal(DriveVerdict.Watch, note.Verdict);
        Assert.Equal("Watch", note.Word);
        Assert.StartsWith("System SSD has a reading that needs attention.", note.Text, StringComparison.Ordinal);
        Assert.Null(_health.Of(@"D:\"));
    }

    [Fact]
    public void AFailingDriveTellsTheReaderToBackUpFirst()
    {
        _health.Record(On(System, @"C:\"));
        _health.Record(Report(System, DriveVerdict.Failing));

        var note = _health.Of(@"C:\");

        Assert.NotNull(note);
        Assert.Equal("Failing", note.Word);
        Assert.Equal("System SSD reports a serious problem. Back up what is on this drive before cleaning it or anything else.", note.Text);
    }

    [Fact]
    public void AVolumeOnSeveralDrivesIsAnsweredForByTheWorst()
    {
        _health.Record(On(System, @"C:\", @"S:\"));
        _health.Record(On(Data, @"S:\"));
        _health.Record(Report(System, DriveVerdict.Watch));
        _health.Record(Report(Data, DriveVerdict.Failing));

        var spanned = _health.Of(@"S:\");

        Assert.NotNull(spanned);
        Assert.Equal(DriveVerdict.Failing, spanned.Verdict);
        Assert.StartsWith("Data SSD reports", spanned.Text, StringComparison.Ordinal);
        Assert.Equal(DriveVerdict.Watch, _health.Of(@"C:\")?.Verdict);
    }

    [Fact]
    public void TwoDrivesOfTheSameConcernAreBothNamed()
    {
        _health.Record(On(System, @"S:\"));
        _health.Record(On(Data, @"S:\"));
        _health.Record(Report(System, DriveVerdict.Failing));
        _health.Record(Report(Data, DriveVerdict.Failing));

        Assert.StartsWith("System SSD and Data SSD report a serious problem.", _health.Of(@"S:\")?.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ADriveNoLongerListedNoLongerSpeaksForItsVolume()
    {
        _health.Record(On(System, @"C:\"));
        _health.Record(On(Data, @"D:\"));
        _health.Record(Report(System, DriveVerdict.Failing));
        _health.Record(Report(Data, DriveVerdict.Failing));
        var changes = Count();

        _health.Listed([System]);

        Assert.Null(_health.Of(@"D:\"));
        Assert.NotNull(_health.Of(@"C:\"));
        Assert.Equal(1, changes());
    }

    [Fact]
    public void AVolumeMovedToAnotherDriveIsAnsweredForByThatDrive()
    {
        _health.Record(On(System, @"C:\", @"E:\"));
        _health.Record(Report(System, DriveVerdict.Failing));
        _health.Record(Report(Data, DriveVerdict.Watch));

        _health.Record(On(System, @"C:\"));
        _health.Record(On(Data, @"E:\"));

        var moved = _health.Of(@"E:\");

        Assert.NotNull(moved);
        Assert.Equal(DriveVerdict.Watch, moved.Verdict);
        Assert.StartsWith("Data SSD", moved.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyAChangeToWhatIsSaidIsRaised()
    {
        _health.Record(On(System, @"C:\"));
        _health.Record(Report(System, DriveVerdict.Watch));
        var changes = Count();

        _health.Record(Report(System, DriveVerdict.Watch));
        _health.Record(On(System, @"c:\"));
        _health.Listed([System]);
        Assert.Equal(0, changes());

        _health.Record(Report(System, DriveVerdict.Good));
        Assert.Equal(1, changes());
        Assert.Null(_health.Of(@"C:\"));
    }

    private Func<int> Count()
    {
        var count = 0;
        _health.Changed += (_, _) => count++;

        return () => count;
    }
}
