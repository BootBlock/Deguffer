using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where a clean was refused, remembered so the next preview can leave out what Windows still
/// refuses (issue #117). What the record has to get right is surviving a restart of the app, being
/// found again whatever spelling a path arrives in, and not growing for ever.
/// </summary>
public sealed class RefusalRecordTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public RefusalRecordTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// The preview after a restart is the one the issue was about: a record held only in memory would
    /// offer everything Windows refused again the next time the app opened.
    /// </summary>
    [Fact]
    public void RemembersWhereACleanWasRefusedAcrossARestart()
    {
        var step = _temp.CreateDirectory("temp");
        var place = _temp.CreateDirectory("temp", "profile");

        RefusalRecord.For(_environment).Replace(step, [place]);

        Assert.Equal([place], new RefusalRecord(_environment).At(step));
    }

    /// <summary>
    /// A clean that was refused nothing asked every file it attempted, so an earlier refusal there is
    /// no longer true and must not go on leaving bytes out of the next preview.
    /// </summary>
    [Fact]
    public void ACleanRefusedNothingClearsWhatAnEarlierOneRecorded()
    {
        var step = _temp.CreateDirectory("temp");
        var place = _temp.CreateDirectory("temp", "profile");
        var record = RefusalRecord.For(_environment);

        record.Replace(step, [place]);
        record.Replace(step, []);

        Assert.Empty(record.At(step));
        Assert.Empty(new RefusalRecord(_environment).At(step));
    }

    /// <summary>
    /// The executor records against the path it removed and the preview asks with the path it
    /// planned. A spelling that missed would leave the row offering everything Windows refuses, with
    /// nothing on screen to say why.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FindsTheEntryWhateverSpellingThePathArrivesIn(bool trailingSeparator, bool extended)
    {
        var step = _temp.CreateDirectory("temp");
        var place = _temp.CreateDirectory("temp", "profile");

        RefusalRecord.For(_environment).Replace(step, [place]);

        var asked = trailingSeparator ? step + Path.DirectorySeparatorChar : step;

        Assert.Equal([place], RefusalRecord.For(_environment).At(extended ? LongPath.Extended(asked) : asked));
    }

    /// <summary>
    /// A location that has gone is never planned again, so nothing would ever clear its entry.
    /// </summary>
    [Fact]
    public void ForgetsALocationThatHasGoneSinceItWasRecorded()
    {
        var step = _temp.CreateDirectory("removed-project", "obj");

        RefusalRecord.For(_environment).Replace(step, [Path.Combine(step, "Debug")]);
        Directory.Delete(step, recursive: true);

        Assert.Empty(new RefusalRecord(_environment).At(step));
    }

    /// <summary>
    /// Every provider records into the one file, and two instances over it would each save their own
    /// view and discard the other's. One profile is one record; another profile is another.
    /// </summary>
    [Fact]
    public void EveryProviderOnOneProfileRecordsIntoOneInstance()
    {
        Assert.Same(RefusalRecord.For(_environment), RefusalRecord.For(new FakeUserEnvironment(_temp.Path)));

        Assert.NotSame(
            RefusalRecord.For(_environment),
            RefusalRecord.For(new FakeUserEnvironment(_temp.CreateDirectory("another-profile"))));
    }

    /// <summary>
    /// A record the process was killed while writing is an empty one. The cost is a preview that
    /// offers what Windows refuses, which the next clean corrects; a failure here would cost the
    /// preview.
    /// </summary>
    [Fact]
    public void AFileThatCannotBeReadIsAnEmptyRecordRatherThanAFailure()
    {
        var file = Path.Combine(_environment.LocalAppData, "Deguffer", "refusals.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ \"half-writ");

        Assert.Empty(new RefusalRecord(_environment).At(_temp.Path));
    }
}
