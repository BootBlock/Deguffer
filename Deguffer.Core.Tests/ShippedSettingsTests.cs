using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Tests;

/// <summary>
/// Each setting the Settings page shows knows its shipped value: it says whether a set of preferences
/// holds it, puts back that one setting and no other, and states it in the words its control shows. The
/// page's restore puts back every one of them and nothing the page does not show.
/// </summary>
public sealed class ShippedSettingsTests
{
    /// <summary>Every setting on the page off its shipped value, every kind of drive's five values included.</summary>
    private static readonly AppPreferences EveryOneChanged = AppPreferences.Default with
    {
        Theme = AppTheme.Dark,
        BackdropEnabled = false,
        TreemapSpacing = ExploreSpacing.Dense,
        ShowAlreadyClear = true,
        SpotlightCleanedRow = false,
        ConfirmBeforeCleaning = false,
        RequireTypedConfirmation = true,
        KeepFilesChangedWithinHours = 8,
        MinimumTemporaryFileAgeDays = 0,
        FileHistoryRetentionDays = 30,
        EmptyRecycleBinsDirectly = true,
        Scanning = ScanTuningText.Kinds.Aggregate(
            ScanPreferences.Default with { Route = ScanRoute.WalkOnly, TableParseThreads = 2 },
            (scanning, media) => scanning.With(media, new MediaScanPreferences(3, 32, 512, 2, 4))),
    };

    /// <summary>
    /// Preferences other pages set where they are seen, and Explore's remembered places, none of which
    /// the Settings page shows.
    /// </summary>
    private static readonly AppPreferences OffThePage = AppPreferences.Default with
    {
        View = ViewDensity.Compact,
        Explore = ExploreView.Sunburst,
        StorageOrder = StorageOrder.Name,
        ShowNotInstalled = true,
        ExploreNotesDismissed = true,
        BackUpInstalledAppEntries = false,
        Rail = RailChoice.Collapsed,
        MemoryLiveUpdate = false,
        Locations = ExploreLocations.None.WithComputer("server.test"),
    };

    [Fact]
    public void EverySettingIsOnItsDefaultInAFreshInstall() =>
        Assert.Empty(ShippedSettings.Changed(AppPreferences.Default));

    [Fact]
    public void EverySettingIsOffItsDefaultInThePreferencesUnderTest() =>
        Assert.Equal(ShippedSettings.All, ShippedSettings.Changed(EveryOneChanged));

    /// <summary>
    /// A row's reset writes its own default and leaves every other setting where the reader put it,
    /// another kind of drive's value for the same box included.
    /// </summary>
    [Fact]
    public void EachResetPutsBackItsOwnSettingAndNoOther()
    {
        foreach (var setting in ShippedSettings.All)
        {
            var reset = setting.Reset(EveryOneChanged);

            Assert.True(setting.IsDefault(reset), $"{setting.Name} is not on its default after its reset.");
            Assert.Equal(
                ShippedSettings.All.Where(other => other != setting),
                ShippedSettings.Changed(reset));
        }
    }

    /// <summary>
    /// Restoring every setting writes the shipped value of each one the page shows, and leaves every
    /// preference it does not show as the reader left it.
    /// </summary>
    [Fact]
    public void RestoringAllPutsBackThePageAndLeavesEveryOtherPreference()
    {
        var mixed = EveryOneChanged with
        {
            View = OffThePage.View,
            Explore = OffThePage.Explore,
            StorageOrder = OffThePage.StorageOrder,
            ShowNotInstalled = OffThePage.ShowNotInstalled,
            ExploreNotesDismissed = OffThePage.ExploreNotesDismissed,
            BackUpInstalledAppEntries = OffThePage.BackUpInstalledAppEntries,
            Rail = OffThePage.Rail,
            MemoryLiveUpdate = OffThePage.MemoryLiveUpdate,
            Locations = OffThePage.Locations,
        };

        Assert.Equal(OffThePage, ShippedSettings.RestoreAll(mixed));
    }

    /// <summary>The default each row states, in the words its control shows.</summary>
    [Fact]
    public void EachDefaultIsStatedAsItsControlShowsIt()
    {
        Assert.Equal("Default: Use system setting", ShippedSettings.Theme.Statement);
        Assert.Equal("Default: Comfortable", ShippedSettings.TreemapSpacing.Statement);
        Assert.Equal("Default: On", ShippedSettings.ConfirmBeforeCleaning.Statement);
        Assert.Equal("Default: Off", ShippedSettings.RequireTypedConfirmation.Statement);
        Assert.Equal("Default: 0 hours", ShippedSettings.KeepFilesChangedWithinHours.Statement);
        Assert.Equal("Default: 7 days", ShippedSettings.MinimumTemporaryFileAgeDays.Statement);
        Assert.Equal("Default: 365 days", ShippedSettings.FileHistoryRetentionDays.Statement);
        Assert.Equal("Default: Auto", ShippedSettings.Route.Statement);
        Assert.Equal("Default: Auto", ShippedSettings.ForKind(StorageMedia.Rotational).ListingBufferKiB.Statement);
    }

    /// <summary>A kind's value is named with its kind wherever several settings are listed together.</summary>
    [Fact]
    public void AKindsValueIsNamedWithItsKind()
    {
        Assert.Equal("Listing buffer (Spinning disk)", ShippedSettings.ForKind(StorageMedia.Rotational).ListingBufferKiB.Name);
        Assert.Equal("Listing buffer", ShippedSettings.ForKind(StorageMedia.Rotational).ListingBufferKiB.Title);
        Assert.Equal("Theme", ShippedSettings.Theme.Name);
    }

    /// <summary>What a setting holds now reads as its default does.</summary>
    [Fact]
    public void WhatASettingHoldsReadsAsItsDefaultDoes()
    {
        Assert.Equal("8 hours", ShippedSettings.KeepFilesChangedWithinHours.Says(EveryOneChanged));
        Assert.Equal("1 day", ShippedSettings.FileHistoryRetentionDays.Says(EveryOneChanged with { FileHistoryRetentionDays = 1 }));
        Assert.Equal("Dark", ShippedSettings.Theme.Says(EveryOneChanged));
        Assert.Equal("Walk folders only", ShippedSettings.Route.Says(EveryOneChanged));
        Assert.Equal("3 threads", ShippedSettings.ForKind(StorageMedia.Nvme).WalkThreads.Says(EveryOneChanged));
        Assert.Equal("32 KB", ShippedSettings.ForKind(StorageMedia.Nvme).ListingBufferKiB.Says(EveryOneChanged));
        Assert.Equal("4 reads", ShippedSettings.ForKind(StorageMedia.Nvme).TableListReadsInFlight.Says(EveryOneChanged));
    }
}
