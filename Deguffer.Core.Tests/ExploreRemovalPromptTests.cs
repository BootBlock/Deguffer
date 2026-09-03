using Deguffer.Core.Exploring.Acting;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the user is told before an Explore removal. A Core type for the reason
/// <see cref="ConfirmationRequirementTests"/>'s subject is one: whether a deletion is reversible,
/// and whether the user was told so, is a safety decision rather than dialog layout.
/// </summary>
public sealed class ExploreRemovalPromptTests
{
    /// <summary>
    /// §8's fourth question, applied to the reversible half. A Recycle Bin removal frees nothing
    /// until the bin is emptied, and a user watching a free-space figure would otherwise reasonably
    /// read this as the moment the space comes back.
    /// </summary>
    [Fact]
    public void TheRecycleBinPromptSaysTheSpaceDoesNotComeBackYet()
    {
        var prompt = ExploreRemovalPrompt.For(
            ExploreRemovalMode.RecycleBin, [new ExploreItem(@"C:\Users\testuser\big.iso", false, 1024)]);

        Assert.Contains("Recycle Bin", prompt.Title, StringComparison.Ordinal);
        Assert.Contains("put it back", prompt.Consequence, StringComparison.Ordinal);
        Assert.Contains("once the bin is emptied", prompt.Consequence, StringComparison.Ordinal);
    }

    /// <summary>
    /// §7.1 makes permanent removal "a deliberate second choice that says what it is". Saying what
    /// it is means saying it does not go to the bin and cannot be undone.
    /// </summary>
    [Fact]
    public void ThePermanentPromptSaysItCannotBeUndone()
    {
        var prompt = ExploreRemovalPrompt.For(
            ExploreRemovalMode.Permanent, [new ExploreItem(@"C:\Users\testuser\big.iso", false, 1024)]);

        Assert.Contains("Permanently", prompt.Title, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", prompt.Consequence, StringComparison.Ordinal);
        Assert.Contains("does not go to the Recycle Bin", prompt.Consequence, StringComparison.Ordinal);
    }

    /// <summary>
    /// §7.1: what Explore shows is not a classification. The prompt says so rather than leaving the
    /// user to read "Deguffer offered to delete it" as "Deguffer says it is safe".
    /// </summary>
    [Fact]
    public void NeitherPromptCallsAnythingSafe()
    {
        foreach (var mode in new[] { ExploreRemovalMode.RecycleBin, ExploreRemovalMode.Permanent })
        {
            var prompt = ExploreRemovalPrompt.For(mode, [new ExploreItem(@"C:\Users\testuser\x", false, 1)]);

            Assert.DoesNotContain("safe", prompt.Consequence, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// One item is named and several are counted. The name is what a user checks the dialog
    /// against, and a list of forty would be a dialog nobody reads.
    /// </summary>
    [Fact]
    public void OneItemIsNamedAndSeveralAreCounted()
    {
        var one = ExploreRemovalPrompt.For(
            ExploreRemovalMode.RecycleBin, [new ExploreItem(@"C:\Users\testuser\big.iso", false, 1)]);

        var several = ExploreRemovalPrompt.For(
            ExploreRemovalMode.RecycleBin,
            [
                new ExploreItem(@"C:\Users\testuser\a", false, 1),
                new ExploreItem(@"C:\Users\testuser\b", false, 1),
            ]);

        Assert.Contains("'big.iso'", one.Title, StringComparison.Ordinal);
        Assert.Contains("2 items", several.Title, StringComparison.Ordinal);
    }
}
