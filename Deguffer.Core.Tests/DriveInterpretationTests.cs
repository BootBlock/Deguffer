using Deguffer.Core.Health;
using Deguffer.Core.Health.Parsing;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.6: what each reading means and whether it is a concern. A concern is decided here once per
/// protocol, so each rule has a case on each side of its line.
/// </summary>
public sealed class DriveInterpretationTests
{
    private static AtaAttribute Attribute(byte id, byte value = 100, ulong raw = 0, bool preFail = false, byte? threshold = null) =>
        new(id, preFail, value, 100, raw, threshold);

    private static HealthAttribute Row(AtaAttribute attribute, string model = "Test Model") =>
        Assert.Single(AtaAttributeRows.Of([attribute], model));

    private static IReadOnlyList<HealthAttribute> Nvme(byte[] health, NvmeController? controller = null)
    {
        Assert.True(NvmeHealthLog.TryRead(health, out var log));
        return NvmeAttributeRows.Of(log!, controller);
    }

    private static HealthAttribute Find(IReadOnlyList<HealthAttribute> rows, string key) =>
        Assert.Single(rows, row => row.Key == key);

    [Fact]
    public void TheDrivesOwnPredictionFailsTheRow()
    {
        Assert.Equal(AttributeStanding.Failing, AtaAttributeRows.Prediction(true).Standing);
        Assert.Equal(AttributeStanding.Normal, AtaAttributeRows.Prediction(false).Standing);
    }

    [Fact]
    public void AFaultCountAboveZeroIsWatch()
    {
        Assert.Equal(AttributeStanding.Watch, Row(Attribute(197, raw: 2)).Standing);
        Assert.Equal(AttributeStanding.Normal, Row(Attribute(197, raw: 0)).Standing);
    }

    [Fact]
    public void AFaultCountReadsItsLowDoubleWordOnly()
    {
        // A drive packing a figure above the count must not turn no faults into a concern.
        Assert.Equal(AttributeStanding.Normal, Row(Attribute(5, raw: 0x0102_0000_0000)).Standing);
    }

    [Fact]
    public void TemperatureIsTheLowByteOfAPackedValue() =>
        Assert.Equal("38 °C", Row(Attribute(194, raw: 0x0014_0030_0026)).Reading);

    [Fact]
    public void AnUnknownAttributeIsVendorSpecificWithItsNumbers()
    {
        var row = Row(Attribute(150, value: 77, raw: 12345));

        Assert.Equal("Vendor-specific attribute 150", row.Name);
        Assert.Contains("12345", row.Raw, StringComparison.Ordinal);
        Assert.Equal(AttributeStanding.Normal, row.Standing);
    }

    [Fact]
    public void AnOpaqueRawValueIsNeverAConcern() =>
        Assert.Equal(AttributeStanding.Normal, Row(Attribute(1, raw: uint.MaxValue)).Standing);

    [Fact]
    public void AValueAtOrBelowAValidThresholdOnAPreFailAttributeFails()
    {
        Assert.Equal(AttributeStanding.Failing, Row(Attribute(1, value: 36, preFail: true, threshold: 36)).Standing);
        Assert.Equal(AttributeStanding.Normal, Row(Attribute(1, value: 37, preFail: true, threshold: 36)).Standing);
    }

    [Fact]
    public void AThresholdDoesNotFailAnAttributeThatIsNotPreFail() =>
        Assert.Equal(AttributeStanding.Normal, Row(Attribute(1, value: 10, preFail: false, threshold: 36)).Standing);

    [Theory]
    [InlineData(0)]
    [InlineData(254)]
    [InlineData(255)]
    public void AReservedThresholdCausesNoConcern(byte threshold) =>
        Assert.Equal(AttributeStanding.Normal, Row(Attribute(1, value: 1, preFail: true, threshold: threshold)).Standing);

    [Fact]
    public void WithNoThresholdNoComparisonIsMade() =>
        Assert.Equal(AttributeStanding.Normal, Row(Attribute(1, value: 1, preFail: true)).Standing);

    [Fact]
    public void AVendorOverlayAppliesOnlyToItsOwnDrives()
    {
        Assert.Equal("Remaining life", Row(Attribute(177, value: 50), "SAMSUNG SSD 860").Name);
        Assert.Equal("Wear levelling count", Row(Attribute(177, value: 50), "Other SSD").Name);
    }

    [Fact]
    public void RemainingLifeAtOrBelowTheLimitIsWatch()
    {
        Assert.Equal(AttributeStanding.Watch, Row(Attribute(177, value: 10), "SAMSUNG SSD").Standing);
        Assert.Equal(AttributeStanding.Normal, Row(Attribute(177, value: 11), "SAMSUNG SSD").Standing);
    }

    [Fact]
    public void ANamedWrittenCounterStatesASize() =>
        Assert.Equal("1.0 MB", Row(Attribute(241, raw: 2048), "SAMSUNG SSD").Reading);

    [Fact]
    public void HoursStateTheTimeTheyComeTo() =>
        Assert.Equal("9,000 hours (1 year 10 days)", HealthText.Hours(9000));

    [Fact]
    public void AnyNvmeWarningOtherThanTemperatureFails()
    {
        Assert.Equal(AttributeStanding.Failing, Find(Nvme(DriveBytes.NvmeHealth(criticalWarning: 0b100)), "critical-warning").Standing);
        Assert.Equal(AttributeStanding.Failing, Find(Nvme(DriveBytes.NvmeHealth(criticalWarning: 0b110)), "critical-warning").Standing);
    }

    [Fact]
    public void AnNvmeTemperatureWarningAloneIsWatch() =>
        Assert.Equal(AttributeStanding.Watch, Find(Nvme(DriveBytes.NvmeHealth(criticalWarning: 0b10)), "critical-warning").Standing);

    [Fact]
    public void NoNvmeWarningIsNormal() =>
        Assert.Equal(AttributeStanding.Normal, Find(Nvme(DriveBytes.NvmeHealth()), "critical-warning").Standing);

    [Theory]
    [InlineData(89, AttributeStanding.Normal)]
    [InlineData(90, AttributeStanding.Watch)]
    public void RatedLifeUsedHasALine(byte percent, AttributeStanding expected) =>
        Assert.Equal(expected, Find(Nvme(DriveBytes.NvmeHealth(percentageUsed: percent)), "life-used").Standing);

    [Fact]
    public void AnyMediaErrorIsWatch()
    {
        Assert.Equal(AttributeStanding.Watch, Find(Nvme(DriveBytes.NvmeHealth(mediaErrors: 1)), "media-errors").Standing);
        Assert.Equal(AttributeStanding.Normal, Find(Nvme(DriveBytes.NvmeHealth()), "media-errors").Standing);
    }

    [Fact]
    public void AnNvmeTemperatureAtItsWarningLimitIsWatch()
    {
        var limit = new NvmeController((1, 4), WarningKelvin: 350);

        Assert.Equal(AttributeStanding.Watch, Find(Nvme(DriveBytes.NvmeHealth(kelvin: 350), limit), "temperature").Standing);
        Assert.Equal(AttributeStanding.Normal, Find(Nvme(DriveBytes.NvmeHealth(kelvin: 349), limit), "temperature").Standing);
    }

    [Fact]
    public void AWarningLimitOfZeroMeansNoneIsReported() =>
        Assert.Equal(
            AttributeStanding.Normal,
            Find(Nvme(DriveBytes.NvmeHealth(kelvin: 350), new NvmeController((1, 4), 0)), "temperature").Standing);

    [Fact]
    public void AFailedSelfTestFails()
    {
        Assert.Equal(AttributeStanding.Failing, AtaAttributeRows.SelfTest(new SelfTestOutcome(SelfTestResult.Failed, 1)).Standing);
        Assert.Equal(AttributeStanding.Normal, AtaAttributeRows.SelfTest(new SelfTestOutcome(SelfTestResult.Stopped, 1)).Standing);
    }

    [Fact]
    public void TheVerdictIsTheWorstStanding()
    {
        var rows = new[] { Plain(AttributeStanding.Normal), Plain(AttributeStanding.Watch), Plain(AttributeStanding.Failing) };

        Assert.Equal(DriveVerdict.Failing, DriveAssessment.Verdict(rows, hasPrimarySignal: true));
        Assert.Equal(DriveVerdict.Watch, DriveAssessment.Verdict(rows[..2], hasPrimarySignal: true));
    }

    [Fact]
    public void GoodNeedsThePrimarySignal()
    {
        var rows = new[] { Plain(AttributeStanding.Normal) };

        Assert.Equal(DriveVerdict.Good, DriveAssessment.Verdict(rows, hasPrimarySignal: true));
        Assert.Equal(DriveVerdict.Unknown, DriveAssessment.Verdict(rows, hasPrimarySignal: false));
    }

    [Fact]
    public void AConcernStillShowsWithoutThePrimarySignal() =>
        Assert.Equal(DriveVerdict.Watch, DriveAssessment.Verdict([Plain(AttributeStanding.Watch)], hasPrimarySignal: false));

    [Fact]
    public void FindingsListOnlyConcernsWorstFirst()
    {
        var findings = DriveAssessment.Findings([Plain(AttributeStanding.Watch, "Warm"), Plain(AttributeStanding.Normal, "Fine"), Plain(AttributeStanding.Failing, "Bad")]);

        Assert.Equal(2, findings.Count);
        Assert.StartsWith("Bad", findings[0], StringComparison.Ordinal);
        Assert.StartsWith("Warm", findings[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ASelectionStillListedStays() =>
        Assert.Equal("b", DriveSelection.After(["a", "b"], "b", 0));

    [Theory]
    [InlineData(0, "a")]
    [InlineData(5, "b")]
    public void AnAbsentSelectionMovesToTheNearestRow(int previous, string expected) =>
        Assert.Equal(expected, DriveSelection.After(["a", "b"], "gone", previous));

    [Fact]
    public void NothingSelectedTakesTheFirstRow() =>
        Assert.Equal("a", DriveSelection.After(["a", "b"], null, 0));

    [Fact]
    public void NoRowsMeansNoSelection() =>
        Assert.Null(DriveSelection.After<string>([], "gone", 0));

    private static HealthAttribute Plain(AttributeStanding standing, string name = "Reading") =>
        new(name, name, "value", "meaning", standing, "raw");
}
