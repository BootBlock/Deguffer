using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the blanket confirmation puts in front of the user, without a WinUI host — the dialog's
/// layout is the shell's business, but what it claims is decided here.
/// </summary>
public sealed class CleanConfirmationTests
{
    /// <summary>
    /// One line per subject, named and sized separately. A single run-on sentence gives the user
    /// nothing to check row by row, which is the last point at which a mistaken selection is
    /// catchable.
    /// </summary>
    [Fact]
    public void ListsEachSubjectSeparatelyInTheOrderSelected()
    {
        var confirmation = CleanConfirmation.For(
        [
            Plan("npm", SafetyTier.RegenerableCache, Gigabytes(3)),
            Plan("NuGet", SafetyTier.RegenerableCache, Gigabytes(1)),
        ]);

        Assert.Equal(["npm", "NuGet"], confirmation.Items.Select(i => i.ProviderName));
        Assert.Equal(["3.0 GB", "1.0 GB"], confirmation.Items.Select(i => i.SizeLabel));
    }

    [Fact]
    public void TotalsTheSubjectsItListed()
    {
        var confirmation = CleanConfirmation.For(
        [
            Plan("npm", SafetyTier.RegenerableCache, Gigabytes(3)),
            Plan("NuGet", SafetyTier.RegenerableCache, Gigabytes(1)),
        ]);

        Assert.Equal("4.0 GB", confirmation.TotalLabel);
    }

    /// <summary>
    /// The whole pipeline, on the mixed selection that makes the total wrong if it is taken from
    /// the screen: the Tier 2 row is asked about separately by §7, so neither its line nor its
    /// bytes may appear in the dialog that authorises the rest.
    /// </summary>
    [Fact]
    public void QuotesNoMoreThanTheDeletionsItAuthorises()
    {
        CleanupPlan[] selection =
        [
            Plan("npm", SafetyTier.RegenerableCache, Gigabytes(3)),
            Plan("Maven", SafetyTier.RegenerableWithCost, Gigabytes(9)),
        ];

        var confirmation = CleanConfirmation.For(ConfirmationRequirement.NotPromptedFor(selection, p => p));

        Assert.Equal(["npm"], confirmation.Items.Select(i => i.ProviderName));
        Assert.Equal("3.0 GB", confirmation.TotalLabel);
    }

    /// <summary>
    /// The dialog tells the user, in one sentence covering every line, that what it lists rebuilds
    /// itself on next use. That sentence is only true because Tier 1 is the only tier §7 leaves to
    /// the blanket ask, so the claim is asserted here rather than left to the wording.
    /// </summary>
    [Theory]
    [InlineData(SafetyTier.RegenerableWithCost)]
    [InlineData(SafetyTier.UserData)]
    [InlineData(SafetyTier.DoNotTouch)]
    public void ListsNothingBeyondTier1(SafetyTier asked)
    {
        CleanupPlan[] selection =
        [
            Plan("npm", SafetyTier.RegenerableCache, Gigabytes(3)),
            Plan("other", asked, Gigabytes(9)),
        ];

        var confirmation = CleanConfirmation.For(ConfirmationRequirement.NotPromptedFor(selection, p => p));

        Assert.Equal(["npm"], confirmation.Items.Select(i => i.ProviderName));
    }

    /// <summary>
    /// §5.5's fallback walk cannot tell allocated bytes from logical ones, and the qualifier is the
    /// difference between reporting a measurement and promising what the deletion returns. It has
    /// to survive both the line and the total, since a total is only as exact as its least exact
    /// part.
    /// </summary>
    [Fact]
    public void KeepsTheQualifierOnAMeasurementThatCouldNotBeExact()
    {
        var confirmation = CleanConfirmation.For(
        [
            Plan("npm", SafetyTier.RegenerableCache, ScanSize.Approximate(3 * BytesPerGigabyte)),
            Plan("NuGet", SafetyTier.RegenerableCache, Gigabytes(1)),
        ]);

        Assert.Equal(["about 3.0 GB", "1.0 GB"], confirmation.Items.Select(i => i.SizeLabel));
        Assert.Equal("about 4.0 GB", confirmation.TotalLabel);
    }

    private const long BytesPerGigabyte = 1024L * 1024 * 1024;

    private static ScanSize Gigabytes(long count) => new(count * BytesPerGigabyte, count * BytesPerGigabyte);

    private static CleanupPlan Plan(string name, SafetyTier tier, ScanSize size) => new()
    {
        ProviderId = name.ToLowerInvariant(),
        ProviderName = name,
        Tier = tier,
        WhatHappensOnNextUse = "Rebuilt on next use.",
        Steps = [new DeleteDirectoryStep($@"C:\Users\testuser\.cache\{name}", "the cache") { Estimated = size }],
    };
}
