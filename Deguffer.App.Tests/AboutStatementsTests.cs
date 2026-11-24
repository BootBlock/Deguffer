using Deguffer.App.Shell;
using Deguffer.Core.Safety;

namespace Deguffer.App.Tests;

/// <summary>
/// The About page describes the whole application from one set of sentences, so what it says about
/// each page can be held to what that page does. A WinUI page cannot be built in a test process, so
/// that the page shows these is observed by driving it.
/// </summary>
public sealed class AboutStatementsTests
{
    /// <summary>The words that claim a removal cannot be taken back.</summary>
    private static readonly string[] Outright = ["outright", "cannot be undone", "no undo"];

    [Fact]
    public void EachTierIsNamedAsTheStorageRowsNameIt() =>
        Assert.Equal(
            Enum.GetValues<SafetyTier>().Select(tier => (tier, tier.ToDisplayName(), tier.ToExplanation())),
            AboutStatements.Tiers.Select(statement => (statement.Tier, statement.Name, statement.Explanation)));

    /// <summary>
    /// The summary names every page the rail opens but About, in the sentence the rail item states,
    /// so the two cannot describe a page two ways.
    /// </summary>
    [Fact]
    public void TheSummaryStatesEveryPageInItsRailWords() =>
        Assert.Equal(
            Enum.GetValues<Destination>()
                .Where(destination => destination != Destination.About)
                .Select(destination => (DestinationNames.Of(destination), DestinationPurposes.Of(destination) + ".")),
            AboutStatements.Purposes.Select(statement => (statement.Name, Assert.Single(statement.Sentences))));

    /// <summary>
    /// Only Storage's clean removes outright with no other route, so a sentence about the whole
    /// application that said so would be false of Explore, Duplicates, Installed apps and Startup.
    /// </summary>
    [Fact]
    public void NoSentenceForTheWholeApplicationClaimsEveryRemovalIsOutright() =>
        Assert.All(
            [AboutStatements.Summary, AboutStatements.PurposesHeading, AboutStatements.TiersIntroduction, AboutStatements.SafeguardsHeading],
            sentence => Assert.DoesNotContain(Outright, word => sentence.Contains(word, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void StorageSaysItsCleanCannotBeUndone() =>
        Assert.Contains(Safeguards(Destination.Storage), sentence => sentence.Contains("cannot be undone", StringComparison.Ordinal));

    /// <summary>
    /// A page with an undo says what it is. An outright removal is named only beside the action that
    /// performs it by name, never as the page's only route.
    /// </summary>
    [Theory]
    [InlineData(Destination.Explore, "Recycle Bin")]
    [InlineData(Destination.Duplicates, "Recycle Bin")]
    [InlineData(Destination.InstalledApps, "restore a backup")]
    [InlineData(Destination.Startup, "Enable lets it start again")]
    public void APageWithAnUndoSaysWhatItIs(Destination destination, string undo)
    {
        var sentences = Safeguards(destination);

        Assert.Contains(sentences, sentence => sentence.Contains(undo, StringComparison.Ordinal));
        Assert.All(
            sentences.Where(sentence => sentence.Contains("outright", StringComparison.Ordinal)),
            sentence => Assert.Contains("Only Delete permanently", sentence, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(Destination.Startup)]
    [InlineData(Destination.Memory)]
    [InlineData(Destination.Health)]
    public void APageThatRemovesNothingClaimsNoRemoval(Destination destination) =>
        Assert.All(
            Safeguards(destination),
            sentence => Assert.DoesNotContain(Outright, word => sentence.Contains(word, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Every page that acts on the machine says what it changes, once, and no page that changes
    /// nothing but Deguffer's own preferences is listed.
    /// </summary>
    [Fact]
    public void EveryPageThatActsOnTheMachineSaysWhatItChangesOnce() =>
        Assert.Equal(
            Enum.GetValues<Destination>()
                .Where(destination => destination is not (Destination.Settings or Destination.About))
                .Select(DestinationNames.Of),
            AboutStatements.Safeguards.Select(statement => statement.Name));

    /// <summary>"Finding" is a word of the code. The interface calls a Storage row a location.</summary>
    [Fact]
    public void NoSentenceUsesTheCodesWordForARow() =>
        Assert.DoesNotContain(
            AllSentences(),
            sentence => sentence.Contains("finding", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void EverySentenceEndsInAFullStop() =>
        Assert.All(
            AboutStatements.Purposes.Concat(AboutStatements.Safeguards).SelectMany(statement => statement.Sentences),
            sentence => Assert.EndsWith(".", sentence, StringComparison.Ordinal));

    private static IReadOnlyList<string> Safeguards(Destination destination) =>
        Assert.Single(AboutStatements.Safeguards, statement => statement.Name == DestinationNames.Of(destination)).Sentences;

    private static IEnumerable<string> AllSentences() =>
        new[] { AboutStatements.Summary, AboutStatements.PurposesHeading, AboutStatements.TiersIntroduction, AboutStatements.SafeguardsHeading }
            .Concat(AboutStatements.Purposes.Concat(AboutStatements.Safeguards).SelectMany(statement => statement.Sentences))
            .Concat(AboutStatements.Tiers.SelectMany(tier => new[] { tier.Name, tier.Explanation }));
}
