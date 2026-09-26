using Deguffer.App.ViewModels;

namespace Deguffer.App.Tests;

/// <summary>What one Storage row says about itself in words that change only with its plan.</summary>
public class FindingRowTextTests
{
    [Fact]
    public void TheItemCountReadsAsEnglishForOneAndForMany()
    {
        Assert.Equal("1 item", new FindingRowText("npm", 1, awaitingSourceFolders: false).ItemsLinkLabel);
        Assert.Equal("40 items", new FindingRowText("npm", 40, awaitingSourceFolders: false).ItemsLinkLabel);
        Assert.StartsWith("One item,", new FindingRowText("npm", 1, awaitingSourceFolders: false).ItemsSentence);
        Assert.StartsWith("40 items,", new FindingRowText("npm", 40, awaitingSourceFolders: false).ItemsSentence);
    }

    /// <summary>
    /// A row with nowhere approved to look has not left anything alone: it has not looked, and its
    /// Contents tab is asking the user for something.
    /// </summary>
    [Fact]
    public void TheContentsTabIsNamedForWhatItHolds()
    {
        Assert.Equal("What this will do", new FindingRowText("npm", 2, awaitingSourceFolders: true).DetailHeader);
        Assert.Equal("What Deguffer needs", new FindingRowText(".NET build output", 0, awaitingSourceFolders: true).DetailHeader);
        Assert.Equal("What was left alone", new FindingRowText("npm", 0, awaitingSourceFolders: false).DetailHeader);
    }

    /// <summary>Every row's links read the same on screen, so each one's accessible name carries the row's own.</summary>
    [Fact]
    public void EachLinkIsNamedForItsRow()
    {
        var text = new FindingRowText("npm", 2, awaitingSourceFolders: false);

        Assert.Equal("Choose from the items in npm", text.ItemsLinkName);
        Assert.Equal("More about npm", text.DetailToggleName);
        Assert.Equal("What is npm?", text.InformationLinkName);
    }
}
