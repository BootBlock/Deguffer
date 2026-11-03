using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A directory a program writes from outside it, which neither the program's executable nor its
/// working directory can show: Squirrel's updater unpacking an update into a staging directory from
/// the application's own folder. The real inspector holds the directory where a program running from
/// one of the declared folders was already running when it was last written, and says it could not
/// tell where either time is unknown.
/// </summary>
public sealed class LiveTreeWriterTests
{
    private const string Staging = @"C:\Users\testuser\AppData\Local\SquirrelTemp";
    private const string Installation = @"C:\Users\testuser\AppData\Local\Chatterbox";

    private static readonly DateTimeOffset Written = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static LiveTreeQuery Query(string name = "tempa") =>
        new(Path.Combine(Staging, name), Staging) { Writers = new LiveTreeWriters([Installation], Written) };

    private static LiveTreeInspector Inspector(params FakeListedProcess[] processes)
    {
        var calls = new FakeProcessTableCalls().With(FakeProcessTableCalls.Own);

        foreach (var process in processes)
        {
            calls.With(process);
        }

        return new LiveTreeInspector(calls, new FakeVolumeInventory());
    }

    private static FakeListedProcess Program(string image, DateTimeOffset? started) => new()
    {
        Id = 4410,
        Name = Path.GetFileNameWithoutExtension(image),
        ImagePath = image,
        StartTime = started,
    };

    /// <summary>
    /// The application's updater, and the application itself where it calls the update manager in its
    /// own process, both run from the installation folder, and either one already running when the
    /// directory was written may be the one writing it.
    /// </summary>
    [Theory]
    [InlineData(Installation + @"\Update.exe")]
    [InlineData(Installation + @"\app-1.0.9254\Chatterbox.exe")]
    public void AProgramFromAWriterFolderRunningBeforeTheLastWriteMakesTheDirectoryLive(string image)
    {
        var query = Query();

        var findings = Inspector(Program(image, Written.AddMinutes(-3))).FindLive([query]);

        Assert.True(findings.Complete);
        var live = Assert.Single(findings.Live);
        Assert.Equal(query.Directory, live.Directory);
        Assert.Contains(live.Holders, h => h.Contains("already running when it was last written", StringComparison.Ordinal));
    }

    /// <summary>
    /// A program started after the directory was last written cannot have made it, so it holds
    /// nothing, and a leftover from an earlier update is still offered (§5.6 runs the other way too:
    /// the rule must not hold every staging directory while an application is open).
    /// </summary>
    [Fact]
    public void AProgramStartedAfterTheLastWriteDoesNotMakeTheDirectoryLive()
    {
        var query = Query();

        var findings = Inspector(Program(Installation + @"\Update.exe", Written.AddMinutes(5))).FindLive([query]);

        Assert.True(findings.Complete);
        Assert.False(findings.IsLive(query.Directory));
        Assert.True(findings.IsAnswered(query.Directory));
    }

    /// <summary>
    /// A timestamp on a coarse file system can trail the write by up to two seconds, so a program
    /// started a moment before the directory it made must not read as started after it.
    /// </summary>
    [Fact]
    public void AProgramStartedWithinTheTimestampToleranceAfterTheLastWriteMakesTheDirectoryLive()
    {
        var query = Query();

        var findings = Inspector(Program(Installation + @"\Update.exe", Written.AddSeconds(1))).FindLive([query]);

        Assert.True(findings.IsLive(query.Directory));
    }

    /// <summary>
    /// The folder is a boundary. A program from another folder whose name merely begins with the
    /// installation's is no evidence, however long it has been running.
    /// </summary>
    [Theory]
    [InlineData(Installation + @"2\Update.exe")]
    [InlineData(@"C:\Program Files\Other\Update.exe")]
    public void AProgramOutsideEveryWriterFolderDoesNotMakeTheDirectoryLive(string image)
    {
        var query = Query();

        var findings = Inspector(Program(image, Written.AddDays(-1))).FindLive([query]);

        Assert.True(findings.Complete);
        Assert.False(findings.IsLive(query.Directory));
    }

    /// <summary>
    /// A writer whose start Windows would not give, or a directory whose last write it would not give,
    /// cannot be compared, and "could not tell" is not "nothing is using it". Only that directory is
    /// unanswered.
    /// </summary>
    [Fact]
    public void AWriterWhoseTimesCannotBeComparedLeavesOnlyThatDirectoryUnanswered()
    {
        var unknownStart = Query("tempa");
        var unknownWrite = Query("tempb") with { Writers = new LiveTreeWriters([Installation], LastWritten: null) };

        var findings = Inspector(Program(Installation + @"\Update.exe", started: null))
            .FindLive([unknownStart]);

        Assert.False(findings.IsAnswered(unknownStart.Directory));
        Assert.False(findings.IsLive(unknownStart.Directory));

        var both = Inspector(Program(Installation + @"\Update.exe", Written.AddMinutes(-3)))
            .FindLive([unknownWrite, Query("tempc")]);

        Assert.False(both.IsAnswered(unknownWrite.Directory));
        Assert.True(both.IsLive(Path.Combine(Staging, "tempc")));
    }

    /// <summary>
    /// An unreadable last write matters only while a writer runs. With none running, there is nothing
    /// to compare it with, and the answer is whole.
    /// </summary>
    [Fact]
    public void AnUnknownLastWriteWithNoWriterRunningIsAnswered()
    {
        var query = Query() with { Writers = new LiveTreeWriters([Installation], LastWritten: null) };

        var findings = Inspector(Program(@"C:\Program Files\Other\other.exe", started: null)).FindLive([query]);

        Assert.True(findings.Complete);
        Assert.False(findings.IsLive(query.Directory));
    }
}
