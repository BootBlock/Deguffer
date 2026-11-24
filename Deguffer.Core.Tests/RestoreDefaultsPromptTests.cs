using Deguffer.Core.Configuration;
using Deguffer.Core.Scanning.Media;

namespace Deguffer.Core.Tests;

/// <summary>
/// The question asked before every setting on the Settings page goes back: it names each setting that
/// will change, with what it holds and what it goes back to, says that the lists the reader built are
/// kept, and is not asked at all where nothing would change.
/// </summary>
public sealed class RestoreDefaultsPromptTests
{
    [Fact]
    public void NothingIsAskedWhereEverySettingIsOnItsDefault() =>
        Assert.Null(RestoreDefaultsPrompt.For(AppPreferences.Default));

    [Fact]
    public void OneChangedSettingIsNamedWithWhatItHoldsAndWhatItGoesBackTo()
    {
        var prompt = RestoreDefaultsPrompt.For(AppPreferences.Default with { KeepFilesChangedWithinHours = 8 });

        Assert.NotNull(prompt);
        Assert.Equal("Restore 1 setting to its default?", prompt.Title);
        Assert.Equal(["Leave recently changed files alone: from 8 hours to 0 hours"], prompt.Items);
        Assert.Equal("Restore defaults", prompt.ConfirmLabel);
        Assert.StartsWith("This setting goes back", prompt.Consequence, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each setting is listed in the page's order, a kind of drive's value with its kind, and only the
    /// settings that would change.
    /// </summary>
    [Fact]
    public void EveryChangedSettingIsListedInThePagesOrder()
    {
        var preferences = AppPreferences.Default with
        {
            Theme = AppTheme.Dark,
            MinimumTemporaryFileAgeDays = 0,
            Scanning = ScanPreferences.Default.With(StorageMedia.Rotational, new MediaScanPreferences(ListingBufferKiB: 64)),
        };

        var prompt = RestoreDefaultsPrompt.For(preferences);

        Assert.NotNull(prompt);
        Assert.Equal("Restore 3 settings to their defaults?", prompt.Title);
        Assert.Equal(
            [
                "Theme: from Dark to Use system setting",
                "Only offer temporary files older than: from 0 days to 7 days",
                "Listing buffer (Spinning disk): from 64 KB to Auto",
            ],
            prompt.Items);
        Assert.StartsWith("These settings go back", prompt.Consequence, StringComparison.Ordinal);
    }

    /// <summary>The lists the reader built one entry at a time are named as kept, so nobody holds back for fear of losing them.</summary>
    [Fact]
    public void TheListsTheReaderBuiltAreSaidToBeKept()
    {
        var prompt = RestoreDefaultsPrompt.For(AppPreferences.Default with { BackdropEnabled = false });

        Assert.NotNull(prompt);
        Assert.Contains("keep list, source folders, emulator folders and scan history are not changed", prompt.Consequence, StringComparison.Ordinal);
    }
}
