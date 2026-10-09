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
        // U+1F600 and U+1F601 share their high half, so a cut by halves would fall inside them.
        var parts = PathDifference.Of(["C:\\A\uD83D\uDE00\\a.jpg", "C:\\A\uD83D\uDE01\\a.jpg"]);

        Assert.Equal("\uD83D\uDE00", parts[0].Differs);
        Assert.Equal("\uD83D\uDE01", parts[1].Differs);
        Assert.Equal(@"C:\A", parts[0].Same);
    }

    /// <summary>Every path comes back whole, in the order it was given.</summary>
    [Fact]
    public void EveryPathComesBackWholeInItsOrder()
    {
        string[] paths = [@"C:\b\x.txt", @"C:\a\x.txt", @"E:\x.txt", @"C:\a\y.txt"];

        Assert.Equal(paths, PathDifference.Of(paths).Select(part => part.ToString()));
    }
}
