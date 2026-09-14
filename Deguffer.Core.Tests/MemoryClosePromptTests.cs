using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the user is asked before a program is asked to close. A Core type for the reason
/// <see cref="ExploreRemovalPromptTests"/>'s subject is one, and more so: §7.2.1 makes this
/// confirmation unconditional, because a posted message cannot be recalled and what is at risk is
/// another program's unsaved work.
/// </summary>
public sealed class MemoryClosePromptTests
{
    private static readonly ProcessMemory Target =
        new(4321, ParentProcessId: 900, "editor.exe", CommitCharge: 200, PrivateWorkingSet: 100, CreationTime: 10);

    /// <summary>
    /// §7.2.1: the dialog names the program and its identifier. The identifier is what tells two
    /// copies of one program apart, and a picture of memory is exactly where a user has two open.
    /// </summary>
    [Fact]
    public void ItNamesTheProgramAndItsIdentifier()
    {
        var prompt = MemoryClosePrompt.For(Target, windows: 1, ServiceListing.Listed);

        Assert.Contains("editor.exe", prompt.Title, StringComparison.Ordinal);
        Assert.Contains("4321", prompt.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// §7.2.1: the count is named, because closing one window of a program that has several does not
    /// close the program, and three save prompts should not be a surprise met afterwards.
    /// </summary>
    [Fact]
    public void OneWindowIsSingularAndSeveralAreCounted()
    {
        var one = MemoryClosePrompt.For(Target, windows: 1, ServiceListing.Listed);
        var several = MemoryClosePrompt.For(Target, windows: 3, ServiceListing.Listed);

        Assert.Contains("its window", one.Consequence, StringComparison.Ordinal);
        Assert.DoesNotContain("1 windows", one.Consequence, StringComparison.Ordinal);
        Assert.Contains("each of its 3 windows", several.Consequence, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two things §7.2.1 requires the user to know before they answer: the program decides what
    /// happens to unsaved work, and Deguffer has nothing stronger to follow up with.
    /// </summary>
    [Fact]
    public void ItSaysTheProgramMayRefuseAndThatDegufferDoesNothingFurther()
    {
        var prompt = MemoryClosePrompt.For(Target, windows: 2, ServiceListing.Listed);

        Assert.Contains("unsaved work", prompt.Consequence, StringComparison.Ordinal);
        Assert.Contains("may refuse", prompt.Consequence, StringComparison.Ordinal);
        Assert.Contains("nothing further", prompt.Consequence, StringComparison.Ordinal);
    }

    /// <summary>
    /// §7.2.1's service row is the one rule in its table that cannot be complete, so the dialog
    /// carries the picture's own sentence about the hosts Windows did not name — in the same words,
    /// from the same place, whichever state the service list came back in.
    /// </summary>
    [Theory]
    [InlineData(ServiceListing.Listed)]
    [InlineData(ServiceListing.ListedInPart)]
    [InlineData(ServiceListing.NotListed)]
    public void ItCarriesThePicturesOwnSentenceAboutTheHostsWindowsDidNotName(ServiceListing listing)
    {
        var prompt = MemoryClosePrompt.For(Target, windows: 1, listing);

        Assert.Contains("service host", prompt.Consequence, StringComparison.Ordinal);
        Assert.Contains(MemoryNotes.WhichHostsAreMissing(listing), prompt.Consequence, StringComparison.Ordinal);
    }

    /// <summary>
    /// §7.2 again: Memory says nothing is safe to close and recommends nothing, and the one dialog
    /// that does propose an action is where that would be easiest to lose.
    /// </summary>
    [Fact]
    public void ItCallsNothingSafeAndRecommendsNothing()
    {
        var prompt = MemoryClosePrompt.For(Target, windows: 1, ServiceListing.Listed);

        foreach (var never in new[] { "safe", "you should", "recommend", "unneeded", "free up" })
        {
            Assert.DoesNotContain(never, prompt.Consequence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(never, prompt.Title, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// §7.2.1 refuses a process with no window that qualifies rather than attempting it, so a
    /// confirmation about none of them is a dialog that should never have been built.
    /// </summary>
    [Fact]
    public void AProcessWithNoQualifyingWindowHasNoConfirmationAtAll() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MemoryClosePrompt.For(Target, windows: 0, ServiceListing.Listed));
}
