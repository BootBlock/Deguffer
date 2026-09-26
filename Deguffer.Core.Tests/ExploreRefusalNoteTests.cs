using Deguffer.Core.Exploring.Acting;

namespace Deguffer.Core.Tests;

/// <summary>
/// The sentence that states, as soon as something is picked, why Explore will not remove it (§7.1:
/// refusals are "stated with their reason rather than by greying something out").
/// </summary>
public sealed class ExploreRefusalNoteTests
{
    private const string Kept = @"C:\Users\testuser\AppData\Local\kept";
    private const string Loose = @"C:\Users\testuser\Downloads\loose.bin";
    private const string Other = @"C:\Users\testuser\Downloads\other.bin";

    private static readonly Func<string, ExploreVerdict> Policy = path => path.StartsWith(Kept, StringComparison.Ordinal)
        ? ExploreVerdict.Refuse($"'{path}' is kept.")
        : ExploreVerdict.Unclassified;

    [Fact]
    public void NothingIsSaidWhereNothingStandsInTheWay()
    {
        Assert.Null(ExploreRefusalNote.For([Item(Loose), Item(Other)], Policy));
        Assert.Null(ExploreRefusalNote.For([], Policy));
    }

    [Fact]
    public void OneRefusedItemIsGivenItsOwnReason()
    {
        Assert.Equal($"'{Kept}' is kept.", ExploreRefusalNote.For([Item(Kept)], Policy));
    }

    /// <summary>
    /// One refusal among several is quoted, and says it is one of them. The reason is the part a
    /// reader acts on, and the count is what keeps it from reading as the whole selection's.
    /// </summary>
    [Fact]
    public void OneRefusalAmongSeveralIsQuotedAndCounted()
    {
        Assert.Equal(
            $"One of these 2 items will not be removed: '{Kept}' is kept.",
            ExploreRefusalNote.For([Item(Kept), Item(Loose)], Policy));
    }

    /// <summary>Several refusals are counted rather than quoted, and the reader is told how to read each.</summary>
    [Fact]
    public void SeveralRefusalsAreCountedAndPointAtTheWayToReadThem()
    {
        var note = ExploreRefusalNote.For([Item(Kept), Item(Kept + @"\a"), Item(Loose)], Policy);

        Assert.Equal(
            "2 of these 3 items will not be removed. Select them one at a time to see why.",
            note);
    }

    private static ExploreItem Item(string path) => new(path, IsDirectory: false, Bytes: 1);
}
