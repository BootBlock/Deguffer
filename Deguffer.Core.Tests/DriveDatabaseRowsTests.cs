using Deguffer.Core.Health;
using Deguffer.Core.Health.Parsing;
using Deguffer.Core.Health.Smartmontools;

namespace Deguffer.Core.Tests;

/// <summary>
/// #751 and §7.6: the installed drive database names only what neither a cited document nor the
/// catalogue reads, judges nothing beyond the drive's own threshold, and is marked as its own.
/// </summary>
public sealed class DriveDatabaseRowsTests
{
    /// <summary>An invented PM863 part number, which Samsung's own document covers for 177.</summary>
    private const string SamsungCovered = "SAMSUNG MZ7LM999HCTE-00000";

    private static DatabaseFamily Family(DriveMedium medium = DriveMedium.SolidState)
    {
        var answer = InventedDriveDatabase.Database().Find(InventedDriveDatabase.Model, "1");

        return new DatabaseFamily(answer.Family!, answer.Presets!, answer.Defaults, medium);
    }

    private static HealthAttribute Row(AtaAttribute attribute, DatabaseFamily? family, string model = InventedDriveDatabase.Model) =>
        Assert.Single(AtaAttributeRows.Of([attribute], AtaDrives.Named(model), logicalSectorBytes: null, family));

    private static AtaAttribute Attribute(byte id, ulong raw, bool preFail = false, byte value = 100, byte? threshold = null) =>
        new(id, preFail, value, value, raw, threshold);

    [Fact]
    public void AnAttributeNothingElseReadsIsNamedAndLaidOutByTheDatabase()
    {
        var row = Row(Attribute(230, 0x000A_0000_0007UL), Family());

        Assert.Equal("Example Paired Count", row.Name);
        Assert.Equal("10 / 7", row.Reading);
        Assert.Equal(AttributeStanding.Normal, row.Standing);
        Assert.Equal(ReadingBasis.DriveDatabase, row.Basis);
        Assert.Contains(InventedDriveDatabase.Family, row.Meaning, StringComparison.Ordinal);
        Assert.Equal("smartmontools", row.BasisLabel);
    }

    [Fact]
    public void WithoutTheDatabaseTheSameAttributeIsOnlyScored()
    {
        var row = Row(Attribute(230, 0x000A_0000_0007UL), family: null);

        Assert.Equal("Vendor drive measure", row.Name);
        Assert.Equal("Scored 100", row.Reading);
        Assert.Equal(ReadingBasis.Deguffer, row.Basis);
        Assert.False(row.IsFromDriveDatabase);
    }

    [Fact]
    public void AnAttributeTheCatalogueCannotReadForEveryDriveTakesTheDatabasesReading()
    {
        // 177's meaning differs by maker, so the catalogue shows only its score.
        var row = Row(Attribute(177, 4_321), Family());

        Assert.Equal("Example Wear Count", row.Name);
        Assert.Equal("4,321", row.Reading);
        Assert.Equal(ReadingBasis.DriveDatabase, row.Basis);
    }

    [Fact]
    public void TheCataloguesDocumentedReadingAndJudgementStand()
    {
        // The database renames 5, and the catalogue's reallocated sectors, judged Watch above zero, stay.
        var row = Row(Attribute(5, 3), Family());

        Assert.Equal("Reallocated sectors", row.Name);
        Assert.Equal(AttributeStanding.Watch, row.Standing);
        Assert.Equal(ReadingBasis.Deguffer, row.Basis);
    }

    [Fact]
    public void AManufacturersDocumentOutranksTheDatabase()
    {
        var row = Row(Attribute(177, 0), Family(), SamsungCovered);

        Assert.Equal("Remaining life", row.Name);
        Assert.Equal(ReadingBasis.Deguffer, row.Basis);
    }

    [Fact]
    public void TheDrivesOwnThresholdStillFailsADatabaseReading()
    {
        var row = Row(Attribute(241, 9, preFail: true, value: 5, threshold: 10), Family());

        Assert.Equal(AttributeStanding.Failing, row.Standing);
        Assert.Equal("Example Total Written", row.Name);
        Assert.Equal(ReadingBasis.DriveDatabase, row.Basis);
    }

    [Fact]
    public void AnAttributeTheEntryDoesNotListIsNotTakenFromTheDefaultEntry()
    {
        // DEFAULT names 9 for every drive, and the entry has no option for 9 or for 100.
        Assert.Equal(ReadingBasis.Deguffer, Row(Attribute(9, 100), Family()).Basis);
        Assert.Equal("Vendor-specific attribute 100", Row(Attribute(100, 100), Family()).Name);
    }

    [Fact]
    public void AnUnnamedOptionTakesTheDefaultEntrysNameForTheKindOfDrive()
    {
        Assert.Equal("Example General Name", Row(Attribute(168, 1), Family(DriveMedium.SolidState)).Name);
        Assert.Equal("Example General Name", Row(Attribute(168, 1), Family(DriveMedium.Any)).Name);
        Assert.Equal("Vendor-specific attribute 168", Row(Attribute(168, 1), Family(DriveMedium.HardDisk)).Name);
        Assert.Equal(ReadingBasis.DriveDatabase, Row(Attribute(168, 1), Family(DriveMedium.HardDisk)).Basis);
    }
}
