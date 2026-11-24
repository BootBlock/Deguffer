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
    /// Every preference the Settings page does not show off its shipped value: what other pages set
    /// where they are seen, how a map is drawn, and Explore's remembered places.
    /// <see cref="TheFixtureMovesEveryPreferenceThePageDoesNotShow"/> holds it to that as preferences are added.
    /// </summary>
    private static readonly AppPreferences OffThePage = AppPreferences.Default with
    {
        View = ViewDensity.Compact,
        Explore = ExploreView.Sunburst,
        ExploreColours = ExploreColouring.Age,
        TreemapScheme = ExploreScheme.Vivid,
        IcicleScheme = ExploreScheme.Soft,
        SunburstScheme = ExploreScheme.Vivid,
        ExploreNotesDismissed = true,
        ExploreOverviewHidden = true,
        MemoryOverviewHidden = true,
        ShowNotInstalled = true,
        StorageOrder = StorageOrder.Name,
        StartupOrder = Startup.StartupOrder.Name,
        StartupOrderDirection = Startup.StartupOrderDirection.Descending,
        BackUpInstalledAppEntries = false,
        WhenCleanComplete = Execution.CompletionAction.Lock,
        Rail = RailChoice.Collapsed,
        MemoryLiveUpdate = false,
        MapStyle = MapStyle.Default with { Shading = MapShading.Flat },
        MapPointerLight = MapPointerLight.Default with { Shown = false },
        MapDetail = MapDetail.Default with { Sizes = false },
        Duplicates = DuplicatePreferences.Default with { SearchHidden = true },
        ChosenToScan = ProviderChoice.None with { Ids = ["npm-cache"] },
        Locations = ExploreLocations.None.WithComputer("server.test"),
    };

    /// <summary>The preferences the page shows, by property, which every other test here reads through <see cref="ShippedSettings"/>.</summary>
    private static readonly string[] PageProperties =
    [
        nameof(AppPreferences.Theme), nameof(AppPreferences.BackdropEnabled), nameof(AppPreferences.TreemapSpacing),
        nameof(AppPreferences.ShowAlreadyClear), nameof(AppPreferences.SpotlightCleanedRow),
        nameof(AppPreferences.ConfirmBeforeCleaning), nameof(AppPreferences.RequireTypedConfirmation),
        nameof(AppPreferences.KeepFilesChangedWithinHours), nameof(AppPreferences.MinimumTemporaryFileAgeDays),
        nameof(AppPreferences.FileHistoryRetentionDays), nameof(AppPreferences.EmptyRecycleBinsDirectly),
        nameof(AppPreferences.Scanning),
    ];

    /// <summary>
    /// The restore test below can only catch a restore that is too wide on a preference this fixture moves,
    /// so it moves every one the page does not show, a preference added later included.
    /// </summary>
    [Fact]
    public void TheFixtureMovesEveryPreferenceThePageDoesNotShow()
    {
        var unmoved = typeof(AppPreferences)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(property => property.CanWrite && !PageProperties.Contains(property.Name))
            .Where(property => Equals(property.GetValue(OffThePage), property.GetValue(AppPreferences.Default)))
            .Select(property => property.Name)
            .ToList();

        Assert.True(unmoved.Count == 0, $"These preferences are still on their defaults in the fixture: {string.Join(", ", unmoved)}.");
    }

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
        var mixed = OffThePage with
        {
            Theme = EveryOneChanged.Theme,
            BackdropEnabled = EveryOneChanged.BackdropEnabled,
            TreemapSpacing = EveryOneChanged.TreemapSpacing,
            ShowAlreadyClear = EveryOneChanged.ShowAlreadyClear,
            SpotlightCleanedRow = EveryOneChanged.SpotlightCleanedRow,
            ConfirmBeforeCleaning = EveryOneChanged.ConfirmBeforeCleaning,
            RequireTypedConfirmation = EveryOneChanged.RequireTypedConfirmation,
            KeepFilesChangedWithinHours = EveryOneChanged.KeepFilesChangedWithinHours,
            MinimumTemporaryFileAgeDays = EveryOneChanged.MinimumTemporaryFileAgeDays,
            FileHistoryRetentionDays = EveryOneChanged.FileHistoryRetentionDays,
            EmptyRecycleBinsDirectly = EveryOneChanged.EmptyRecycleBinsDirectly,
            Scanning = EveryOneChanged.Scanning,
        };

        Assert.Equal(ShippedSettings.All, ShippedSettings.Changed(mixed));

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
        Assert.Equal("Default: 7 days", ShippedSettings.MinimumTemporaryFileAge.Statement);
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
