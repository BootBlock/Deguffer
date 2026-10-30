using Deguffer.Core.Duplicates;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where each path of a group differs from the others (§7.4), which is what a user reads to tell two
/// near-identical folders apart before marking the copy they meant to keep.
/// </summary>
public sealed class PathDifferenceTests
{
    /// <summary>One letter apart: the letter is what differs, and the rest of each path is the same.</summary>
    [Fact]
    public void TwoFoldersOneLetterApartDifferByThatLetter()
    {
        var parts = PathDifference.Of([@"C:\Photos 2023\a.jpg", @"C:\Photos 2O23\a.jpg"]);

        Assert.Equal(new PathParts(@"C:\Photos 2", "0", @"23\a.jpg"), parts[0]);
        Assert.Equal(new PathParts(@"C:\Photos 2", "O", @"23\a.jpg"), parts[1]);
    }

    /// <summary>
    /// A third copy unlike either does not hide how little the two near twins differ by: each path
    /// is compared with whichever other shares the most of its start, and of its end.
    /// </summary>
    [Fact]
    public void AnUnlikeCopyDoesNotHideHowLittleTwoOthersDiffer()
    {
        var parts = PathDifference.Of([@"D:\Backup\a.jpg", @"C:\Photos 2023\a.jpg", @"C:\Photos 2O23\a.jpg"]);

        Assert.Equal("0", parts[1].Differs);
        Assert.Equal("O", parts[2].Differs);
        Assert.Equal(@"D:\Backup", parts[0].Differs);
        Assert.Equal(@"\a.jpg", parts[0].SameEnd);
    }

    /// <summary>
    /// The two near twins are compared with each other, whatever sorts before them: here a copy on
    /// another drive sorts first, and a path that differs from it at once still shares its start
    /// with its twin.
    /// </summary>
    [Fact]
    public void ANearTwinIsFoundWhateverSortsFirst()
    {
        var parts = PathDifference.Of([@"A:\x\a.jpg", @"C:\Photos 2023\a.jpg", @"C:\Photos 2O23\a.jpg"]);

        Assert.Equal("0", parts[1].Differs);
        Assert.Equal("O", parts[2].Differs);
    }

    /// <summary>
    /// The paths are ordered case and all. Ordered without regard to case, <c>AB5</c> would fall
    /// between <c>ab1</c> and <c>ab9</c>, and those two would not be found to share <c>ab</c>.
    /// </summary>
    [Fact]
    public void ThePathsAreOrderedCaseAndAll()
    {
        var parts = PathDifference.Of([@"C:\ab1\x", @"C:\AB5\x", @"C:\ab9\x"]);

        Assert.Equal("1", parts[0].Differs);
        Assert.Equal("9", parts[2].Differs);
    }

    /// <summary>Case is a difference: a case-sensitive folder can hold both.</summary>
    [Fact]
    public void CaseIsADifference()
    {
        var parts = PathDifference.Of([@"C:\Work\photos\a.jpg", @"C:\Work\Photos\a.jpg"]);

        Assert.Equal("p", parts[0].Differs);
        Assert.Equal("P", parts[1].Differs);
    }

    /// <summary>
    /// Where one path is the other with something added, the shorter differs by nothing and the
    /// longer by what was added, and neither start nor end is counted twice.
    /// </summary>
    [Fact]
    public void AnAddedFolderIsWhatTheLongerPathDiffersBy()
    {
        var parts = PathDifference.Of([@"C:\Docs\PowerShell\a.dll", @"C:\Docs\WindowsPowerShell\a.dll"]);

        Assert.Equal(string.Empty, parts[0].Differs);
        Assert.Equal(@"C:\Docs\PowerShell\a.dll", parts[0].ToString());
        Assert.Equal("Windows", parts[1].Differs);
        Assert.Equal(@"C:\Docs\WindowsPowerShell\a.dll", parts[1].ToString());
    }

    /// <summary>A character written as two halves is never cut between them, which would show as two broken characters.</summary>
    [Fact]
    public void ACharacterWrittenAsTwoHalvesIsNeverCut()
    {
        // U+1F600 and U+1F601 share their high half, so a cut by halves would fall inside them. The
        // folder's name is long enough that the two are near twins, which are read by the letter.
        var parts = PathDifference.Of(["C:\\Album\uD83D\uDE00\\a.jpg", "C:\\Album\uD83D\uDE01\\a.jpg"]);

        Assert.Equal("\uD83D\uDE00", parts[0].Differs);
        Assert.Equal("\uD83D\uDE01", parts[1].Differs);
        Assert.Equal(@"C:\Album", parts[0].Same);
    }

    /// <summary>
    /// Two names that share only a letter or two are not near twins, so the difference is the whole
    /// name, rather than one starting after the letters they happen to share.
    /// </summary>
    [Fact]
    public void NamesThatShareLittleDifferByTheWholeName()
    {
        var parts = PathDifference.Of([@"C:\Users\x\Documents\a.jpg", @"C:\Users\x\Downloads\a.jpg"]);

        Assert.Equal(new PathParts(@"C:\Users\x\", "Documents", @"\a.jpg"), parts[0]);
        Assert.Equal(new PathParts(@"C:\Users\x\", "Downloads", @"\a.jpg"), parts[1]);
    }

    /// <summary>A difference that runs across a separator is widened to every name it touches.</summary>
    [Fact]
    public void ADifferenceAcrossFoldersCoversTheirWholeNames()
    {
        var parts = PathDifference.Of([@"D:\Demo\Documents\Shared album\a.jpg", @"D:\Demo\Downloads\Phone backup\a.jpg"]);

        Assert.Equal(new PathParts(@"D:\Demo\", @"Documents\Shared album", @"\a.jpg"), parts[0]);
        Assert.Equal(new PathParts(@"D:\Demo\", @"Downloads\Phone backup", @"\a.jpg"), parts[1]);
    }

    /// <summary>
    /// File names that share only their extension differ by the whole name, extension included,
    /// rather than leaving <c>.jpg</c> unmarked as if it were what made them twins.
    /// </summary>
    [Fact]
    public void FileNamesThatShareOnlyTheirExtensionDifferWhole()
    {
        var parts = PathDifference.Of([@"C:\Pictures\holiday.jpg", @"C:\Pictures\beach.jpg"]);

        Assert.Equal(new PathParts(@"C:\Pictures\", "holiday.jpg", string.Empty), parts[0]);
        Assert.Equal(new PathParts(@"C:\Pictures\", "beach.jpg", string.Empty), parts[1]);
    }

    /// <summary>The copy a browser or Explorer names by adding a number is a near twin, and is read by the letter.</summary>
    [Fact]
    public void ANumberedCopyDiffersByItsNumber()
    {
        var parts = PathDifference.Of([@"C:\Downloads\quote.pdf", @"C:\Downloads\quote (1).pdf"]);

        Assert.Equal(string.Empty, parts[0].Differs);
        Assert.Equal(" (1)", parts[1].Differs);
    }

    /// <summary>Every path comes back whole, in the order it was given.</summary>
    [Fact]
    public void EveryPathComesBackWholeInItsOrder()
    {
        string[] paths = [@"C:\b\x.txt", @"C:\a\x.txt", @"E:\x.txt", @"C:\a\y.txt"];

        Assert.Equal(paths, PathDifference.Of(paths).Select(part => part.ToString()));
    }
}
