using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests;

/// <summary>
/// Deguffer's own private working set is what the undocumented table is checked against, so which
/// documented source gives it decides whether the check can run at all on an older Windows (§7.2).
/// These prove the newer counter is used where it answers, that anything else reaches the fallback,
/// and what the fallback's own answer counts.
/// </summary>
public sealed class OwnPrivateWorkingSetTests
{
    private const long Page = 4096;

    /// <summary>The fallback's shareable bit, and two of the protection bits beneath it, which must not be mistaken for it.</summary>
    private const nuint Shared = 0x100;
    private const nuint Protection = 0x06;

    [Fact]
    public void TheCountersFigureIsUsedWithoutWalking() =>
        Assert.Equal(5_000_000, OwnPrivateWorkingSet.Choose(counterAnswered: true, 5_000_000, NotWalked));

    /// <summary>A running process always has private pages, so a zero is a field this Windows did not fill.</summary>
    [Fact]
    public void AZeroFromTheCounterMeansItDidNotAnswer() =>
        Assert.Equal(42, OwnPrivateWorkingSet.Choose(counterAnswered: true, 0, () => 42));

    /// <summary>Whatever a failed call left in the field is not a figure.</summary>
    [Fact]
    public void AFailedCallReachesTheFallback() =>
        Assert.Equal(42, OwnPrivateWorkingSet.Choose(counterAnswered: false, 5_000_000, () => 42));

    [Fact]
    public void NeitherAnsweringLeavesNoFigure() =>
        Assert.Null(OwnPrivateWorkingSet.Choose(counterAnswered: false, 0, () => null));

    /// <summary>
    /// Four entries, two of them shareable, and a fifth past the count that must not be counted. No bit
    /// is set on more than one entry except the shareable bit itself, and the protection bits sit on one
    /// private entry alone, so a mask on any other bit counts a different number of pages rather than
    /// the same number by coincidence.
    /// </summary>
    [Fact]
    public void OnlyPagesWindowsDoesNotMarkShareableArePrivate()
    {
        nuint[] information =
        [
            4,
            0x0001_0000 | Shared,
            0x0002_0000,
            0x0004_0000 | Protection,
            0x0008_0000 | Shared,
            0x0010_0000,
        ];

        Assert.Equal(2 * Page, OwnPrivateWorkingSet.PrivateBytes(information, Page));
    }

    [Fact]
    public void ACountPastTheBufferIsReadOnlyAsFarAsTheBuffer() =>
        Assert.Equal(2 * Page, OwnPrivateWorkingSet.PrivateBytes([10, 0x1000_0000, 0x2000_0000], Page));

    private static long? NotWalked() =>
        throw new InvalidOperationException("The working set was walked although the counter answered.");
}
