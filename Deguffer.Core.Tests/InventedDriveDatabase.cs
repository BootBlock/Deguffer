using Deguffer.Core.Health.Smartmontools;

namespace Deguffer.Core.Tests;

/// <summary>
/// A drive database written for the tests, in the format smartmontools uses. Every family, pattern and
/// name is invented, because the real database is GPL and none of it is copied into Deguffer.
/// </summary>
internal static class InventedDriveDatabase
{
    internal const string Family = "Example Test SSDs";
    internal const string Model = "EXAMPLE SSD 512GB";
    internal const string FirmwareFamily = "Example Firmware Pinned SSDs";
    internal const string PinnedModel = "EXAMPLE PINNED 1TB";
    internal const string PinnedFirmware = "PIN001";

    internal const string Text = """
        { "VERSION: 9.9 test", "-", "-", "Version information", "" },
        { "DEFAULT", "-", "-", "Default settings",
          "-v 9,raw24(raw8),Power_On_Hours "
          "-v 10,raw48,Spin_Retry_Count,HDD "
          "-v 168,raw48,Example_General_Name,SSD "
          "-v 170,raw48,Example_Reserve,SSD "
        },
        { "USB: Example Bridge; Example", "0x1234:0x5678", "", "", "-d sat" },
        { "$Example ignored family", "EXAMPLE SSD .*", "", "", "-v 200,raw48,Ignored" },
        { "Example Firmware Pinned SSDs",
          "EXAMPLE PINNED [0-9]+TB", "PIN00[0-9]", "",
          "-v 200,raw48,Pinned_Count"
        },
        { "Example Test SSDs",
          "EXAMPLE SSD [0-9]+GB", "", "",
          "-v 5,raw48,Example_Retired_Blocks "
          "-v 168,raw48 "
          "-v 170,raw48 "
          "-v 177,raw48,Example_Wear_Count "
          "-v 230,raw24/raw24:z54z10,Example_Paired_Count "
          "-v 241,raw48,Example_Total_Written "
        },
        { "Example catch-all", "EXAMPLE .*", "", "", "-v 200,raw48,Catch_All" },
        """;

    internal static DriveDatabase Database(string? before = null) =>
        new(DriveDatabaseFile.Read((before ?? string.Empty) + Text).Entries!);
}
