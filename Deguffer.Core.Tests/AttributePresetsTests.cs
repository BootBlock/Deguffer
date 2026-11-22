using Deguffer.Core.Health.Smartmontools;

namespace Deguffer.Core.Tests;

/// <summary>#751: an entry's <c>-v</c> options, read whole or not at all.</summary>
public sealed class AttributePresetsTests
{
    private static EntryPresets Read(string presets, bool isDefault = false) => AttributePresets.Read(presets, isDefault)!;

    [Fact]
    public void ReadsAnIdAFormatAByteOrderAndAName()
    {
        var presets = Read("-v 170,raw24/raw24:z54z10,Spare_Blocks -v 9,msec24hour32");

        Assert.Equal(new AttributePreset(DatabaseRawFormat.Raw24OverRaw24, "z54z10", "Spare_Blocks", DriveMedium.Any), presets.Of(170));
        Assert.Equal(new AttributePreset(DatabaseRawFormat.HoursAndMilliseconds, string.Empty, null, DriveMedium.Any), presets.Of(9));
        Assert.Null(presets.Of(1));
    }

    [Fact]
    public void ALaterOptionForAnIdReplacesAnEarlierOne() =>
        Assert.Equal("Second", Read("-v 9,raw48,First -v 9,raw48,Second").Of(9)!.Name);

    [Fact]
    public void ReadsTheOlderSpellingsAsTheirCurrentForms()
    {
        var presets = Read("-v 9,minutes -v 193,loadunload -v 197,increasing");

        Assert.Equal(new AttributePreset(DatabaseRawFormat.Minutes, string.Empty, "Power_On_Minutes", DriveMedium.Any), presets.Of(9));
        Assert.Equal(DatabaseRawFormat.Raw24OverRaw24, presets.Of(193)!.Format);
        Assert.Equal("Total_Pending_Sectors", presets.Of(197)!.Name);
    }

    [Fact]
    public void AnIdsOwnOptionOutranksNWhateverTheOrder()
    {
        var presets = Read("-v N,raw16 -v 5,hex48 -v N,raw8");

        Assert.Equal(DatabaseRawFormat.Hex48, presets.Of(5)!.Format);
        Assert.Equal(DatabaseRawFormat.Raw16, presets.Of(200)!.Format);
    }

    [Fact]
    public void FirmwareAndUsbOptionsSayNothingAboutAttributes() =>
        Assert.Equal(DatabaseRawFormat.Raw48, Read("-F samsung -v 9,raw48 -d sat").Of(9)!.Format);

    [Fact]
    public void OnlyTheDefaultEntryNamesTheKindOfDrive()
    {
        Assert.Equal(DriveMedium.HardDisk, Read("-v 10,raw48,Spin_Retry_Count,HDD", isDefault: true).Of(10)!.Only);
        Assert.Equal(DriveMedium.SolidState, Read("-v 177,raw48,Wear_Leveling_Count,SSD", isDefault: true).Of(177)!.Only);
        Assert.Null(AttributePresets.Read("-v 10,raw48,Spin_Retry_Count,HDD", isDefault: false));
    }

    [Theory]
    [InlineData("-v 9,raw49")]
    [InlineData("-v 9,raw48:6543210")]
    [InlineData("-v 9,raw48:")]
    [InlineData("-v 9,raw48:012345012")]
    [InlineData("-v 0,raw48")]
    [InlineData("-v 256,raw48")]
    [InlineData("-v x9,raw48")]
    [InlineData("-v 9")]
    [InlineData("-v 9,raw48,")]
    [InlineData("-v 9,raw48,A_Name_Longer_Than_Thirty_Two_Chars")]
    [InlineData("-v 9,raw48,Name,SSD")]
    [InlineData("-v 9,raw48,Name,XYZ,More")]
    [InlineData("-v 9,raw48 -x 1")]
    [InlineData("-v 9,raw48 -v")]
    [InlineData("-v 1,raw48 -v 9,unknownformat,Name")]
    public void OneOptionNotReadFailsTheWholeEntry(string presets) => Assert.Null(AttributePresets.Read(presets, isDefault: false));

    [Fact]
    public void TheDefaultEntryMayNotReorderBytes() => Assert.Null(AttributePresets.Read("-v 9,raw48:012345,Name", isDefault: true));
}
