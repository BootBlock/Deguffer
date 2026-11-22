using Deguffer.Core.Health.Smartmontools;

namespace Deguffer.Core.Tests;

/// <summary>#751: searching a drive database as smartctl does, in order, the first match winning.</summary>
public sealed class DriveDatabaseTests
{
    [Fact]
    public void TheFirstEntryThatMatchesTheWholeModelWins()
    {
        var answer = InventedDriveDatabase.Database().Find(InventedDriveDatabase.Model, "ANY1");

        Assert.Equal(DatabaseVerdict.Found, answer.Verdict);
        Assert.Equal(InventedDriveDatabase.Family, answer.Family);
        Assert.Equal("Example_Wear_Count", answer.Presets!.Of(177)!.Name);
    }

    [Fact]
    public void AnEntryWithAFirmwarePatternNamesOnlyThatFirmware()
    {
        var database = InventedDriveDatabase.Database();

        Assert.Equal(InventedDriveDatabase.FirmwareFamily, database.Find(InventedDriveDatabase.PinnedModel, InventedDriveDatabase.PinnedFirmware).Family);
        Assert.Equal("Example catch-all", database.Find(InventedDriveDatabase.PinnedModel, "OTHER1").Family);
    }

    [Fact]
    public void VersionDefaultUsbAndDollarEntriesNameNoDrive()
    {
        var database = InventedDriveDatabase.Database();

        Assert.Equal(DatabaseVerdict.NotListed, database.Find("DEFAULT", string.Empty).Verdict);
        Assert.Equal(DatabaseVerdict.NotListed, database.Find("-", "-").Verdict);
        Assert.Equal(DatabaseVerdict.NotListed, database.Find("0x1234:0x5678", string.Empty).Verdict);

        // The $ entry would match first, and is passed over.
        Assert.Equal(InventedDriveDatabase.Family, database.Find(InventedDriveDatabase.Model, string.Empty).Family);
    }

    [Fact]
    public void ADriveNoEntryNamesIsNotListed() =>
        Assert.Same(DatabaseAnswer.NotListed, InventedDriveDatabase.Database().Find("OTHERCO DRIVE", "1"));

    [Fact]
    public void AnUntranslatedPatternBeforeTheMatchMeansTheDatabaseCannotTell()
    {
        var database = InventedDriveDatabase.Database("""{ "Example class family", "EXAMPLE[[:digit:]]", "", "", "" },""");

        Assert.Equal(DatabaseVerdict.CannotTell, database.Find(InventedDriveDatabase.Model, "1").Verdict);
    }

    [Fact]
    public void AnUntranslatedFirmwarePatternOfAnotherModelIsNeverReached()
    {
        var database = InventedDriveDatabase.Database("""{ "Other family", "OTHERCO .*", "[[:digit:]]", "", "" },""");

        Assert.Equal(DatabaseVerdict.Found, database.Find(InventedDriveDatabase.Model, "1").Verdict);
    }

    [Fact]
    public void AnUntranslatedPatternAfterTheMatchIsNeverReached()
    {
        var entries = DriveDatabaseFile.Read(InventedDriveDatabase.Text + """{ "Late family", "EXAMPLE[[:digit:]]", "", "", "" },""").Entries!;

        Assert.Equal(DatabaseVerdict.Found, new DriveDatabase(entries).Find(InventedDriveDatabase.Model, "1").Verdict);
    }

    [Fact]
    public void AMatchedEntryWhoseOptionsCannotBeReadIsNotUsed()
    {
        var database = InventedDriveDatabase.Database("""{ "Example broken family", "EXAMPLE SSD .*", "", "", "-v 9,notaformat" },""");
        var answer = database.Find(InventedDriveDatabase.Model, "1");

        Assert.Equal(DatabaseVerdict.EntryUnreadable, answer.Verdict);
        Assert.Equal("Example broken family", answer.Family);
        Assert.Null(answer.Presets);
    }

    [Fact]
    public void TheFirstDefaultEntryIsTheOneUsed()
    {
        var database = InventedDriveDatabase.Database("""{ "DEFAULT", "-", "-", "", "-v 168,raw48,Added_General_Name" },""");

        Assert.Equal("Added_General_Name", database.Find(InventedDriveDatabase.Model, "1").Defaults!.Of(168)!.Name);
    }
}
