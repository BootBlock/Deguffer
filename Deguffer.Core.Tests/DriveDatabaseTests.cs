using Deguffer.Core.Health.Smartmontools;

namespace Deguffer.Core.Tests;

/// <summary>#751: searching a drive database as smartctl does, in order, the first match winning.</summary>
public sealed class DriveDatabaseTests
{
    private static DatabaseAnswer.Found Found(DatabaseAnswer answer) => Assert.IsType<DatabaseAnswer.Found>(answer);

    [Fact]
    public void TheFirstEntryThatMatchesTheWholeModelWins()
    {
        var answer = Found(InventedDriveDatabase.Database().Find(InventedDriveDatabase.Model, "ANY1"));

        Assert.Equal(InventedDriveDatabase.Family, answer.Name);
        Assert.Equal(InventedDriveDatabase.Family, answer.Detail);
        Assert.Equal("Example_Wear_Count", answer.Presets.Of(177)!.Name);
    }

    [Fact]
    public void AnEntryWithAFirmwarePatternNamesOnlyThatFirmware()
    {
        var database = InventedDriveDatabase.Database();

        Assert.Equal(InventedDriveDatabase.FirmwareFamily, Found(database.Find(InventedDriveDatabase.PinnedModel, InventedDriveDatabase.PinnedFirmware)).Name);
        Assert.Equal("Example catch-all", Found(database.Find(InventedDriveDatabase.PinnedModel, "OTHER1")).Name);
    }

    [Fact]
    public void VersionDefaultUsbAndDollarEntriesNameNoDrive()
    {
        var database = InventedDriveDatabase.Database();

        Assert.Same(DatabaseAnswer.NotListed, database.Find("DEFAULT", string.Empty));
        Assert.Same(DatabaseAnswer.NotListed, database.Find("-", "-"));
        Assert.Same(DatabaseAnswer.NotListed, database.Find("0x1234:0x5678", string.Empty));

        // The $ entry would match first, and is passed over.
        Assert.Equal(InventedDriveDatabase.Family, Found(database.Find(InventedDriveDatabase.Model, string.Empty)).Name);
    }

    [Fact]
    public void ADriveNoEntryNamesIsNotListed()
    {
        var answer = InventedDriveDatabase.Database().Find("OTHERCO DRIVE", "1");

        Assert.Same(DatabaseAnswer.NotListed, answer);
        Assert.Equal("No entry for this drive", answer.Detail);
        Assert.Null(answer.Family(DriveMedium.Any));
    }

    [Fact]
    public void TheOtherSpellingOfTheModelIsTriedOnlyWhereTheFirstIsNotListed()
    {
        var database = InventedDriveDatabase.Database();

        Assert.Equal(InventedDriveDatabase.Family, Found(database.Find(new DriveIdentity("ATA " + InventedDriveDatabase.Model, InventedDriveDatabase.Model, "1"))).Name);
        Assert.Equal("Example catch-all", Found(database.Find(new DriveIdentity("EXAMPLE OTHER", InventedDriveDatabase.Model, "1"))).Name);
        Assert.Same(DatabaseAnswer.NotListed, database.Find(new DriveIdentity("ATA " + InventedDriveDatabase.Model, null, "1")));
    }

    [Fact]
    public void AnUntranslatedPatternBeforeTheMatchMeansTheDatabaseCannotTell()
    {
        var database = InventedDriveDatabase.Database("""{ "Example class family", "EXAMPLE[[:digit:]]", "", "", "" },""");
        var answer = database.Find(InventedDriveDatabase.Model, "1");

        Assert.Same(DatabaseAnswer.CannotTell, answer);
        Assert.Null(answer.Family(DriveMedium.Any));
    }

    [Fact]
    public void AnUntranslatedFirmwarePatternOfAnotherModelIsNeverReached()
    {
        var database = InventedDriveDatabase.Database("""{ "Other family", "OTHERCO .*", "[[:digit:]]", "", "" },""");

        Found(database.Find(InventedDriveDatabase.Model, "1"));
    }

    [Fact]
    public void AnUntranslatedPatternAfterTheMatchIsNeverReached()
    {
        var entries = InventedDriveDatabase.Entries(InventedDriveDatabase.Text + """{ "Late family", "EXAMPLE[[:digit:]]", "", "", "" },""");

        Found(new DriveDatabase(entries).Find(InventedDriveDatabase.Model, "1"));
    }

    [Fact]
    public void AMatchedEntryWhoseOptionsCannotBeReadIsNotUsed()
    {
        var database = InventedDriveDatabase.Database("""{ "Example broken family", "EXAMPLE SSD .*", "", "", "-v 9,notaformat" },""");
        var answer = Assert.IsType<DatabaseAnswer.EntryUnreadable>(database.Find(InventedDriveDatabase.Model, "1"));

        Assert.Equal("Example broken family", answer.Name);
        Assert.Null(answer.Family(DriveMedium.Any));
    }

    [Fact]
    public void TheFirstDefaultEntryIsTheOneUsed()
    {
        var database = InventedDriveDatabase.Database("""{ "DEFAULT", "-", "-", "", "-v 168,raw48,Added_General_Name" },""");

        Assert.Equal("Added_General_Name", Found(database.Find(InventedDriveDatabase.Model, "1")).Defaults!.Of(168)!.Name);
    }
}
