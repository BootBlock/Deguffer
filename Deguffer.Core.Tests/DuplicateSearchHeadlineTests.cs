using Deguffer.Core.Duplicates;

namespace Deguffer.Core.Tests;

/// <summary>What the Duplicates page says the search came to, as a title and a sentence.</summary>
public sealed class DuplicateSearchHeadlineTests
{
    public static TheoryData<DuplicateSearchHeadline> Every =>
    [
        DuplicateSearchHeadline.NotSearched,
        DuplicateSearchHeadline.Searching,
        DuplicateSearchHeadline.Arriving,
        DuplicateSearchHeadline.StoppedWhileFinding,
        DuplicateSearchHeadline.OutOfMemory(anyGroup: false),
        DuplicateSearchHeadline.OutOfMemory(anyGroup: true),
        DuplicateSearchHeadline.Finished(stopped: false, anyGroup: false),
        DuplicateSearchHeadline.Finished(stopped: false, anyGroup: true),
        DuplicateSearchHeadline.Finished(stopped: true, anyGroup: false),
        DuplicateSearchHeadline.Finished(stopped: true, anyGroup: true),
    ];

    /// <summary>
    /// The empty state's title is a few words over the sentence, as every other page's is, and never
    /// the sentence again or a sentence of its own.
    /// </summary>
    [Theory]
    [MemberData(nameof(Every))]
    public void TheTitleIsAFewWordsOverADifferentSentence(DuplicateSearchHeadline headline)
    {
        Assert.InRange(headline.Title.Split(' ').Length, 1, 4);
        Assert.False(headline.Title.EndsWith('.'), $"'{headline.Title}' is a sentence, not a title.");
        Assert.NotEqual(headline.Title, headline.Sentence);
        Assert.EndsWith(".", headline.Sentence, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a search that ran to its end may say no two files matched. A stop leaves files
    /// uncompared, so it says only that nothing was confirmed.
    /// </summary>
    [Fact]
    public void ASearchThatFoundNothingSaysSoInTheTitleAndWhyInTheSentence()
    {
        Assert.Equal(
            new DuplicateSearchHeadline("No duplicates found", "No two files in the locations searched matched."),
            DuplicateSearchHeadline.Finished(stopped: false, anyGroup: false));
        Assert.Equal(
            new DuplicateSearchHeadline("Search stopped", "Stopped before any duplicates were confirmed."),
            DuplicateSearchHeadline.Finished(stopped: true, anyGroup: false));
    }

    /// <summary>Groups confirmed before memory ran out stay, so files in none of them may still have copies.</summary>
    [Fact]
    public void RunningOutOfMemorySaysWhatTheGroupsMayMiss()
    {
        Assert.Contains("may still have copies", DuplicateSearchHeadline.OutOfMemory(anyGroup: true).Sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("may still have copies", DuplicateSearchHeadline.OutOfMemory(anyGroup: false).Sentence, StringComparison.Ordinal);
    }
}
