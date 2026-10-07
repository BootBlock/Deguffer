using Deguffer.Core.InstalledApps;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What an entry's row shows beside its name (§7.3): its standing with the reason behind it, why
/// Windows hides it, the shield, and the size its installer recorded.
/// </summary>
public sealed class EntryMarksTests : IDisposable
{
    private static readonly ActionVerdict Allowed = ActionVerdict.Allow("Runs it.");

    private static readonly ActionVerdict Refused = ActionVerdict.Refuse("No.");

    private readonly TempDirectory _temp = new();

    private readonly FakeUninstallRegistry _registry = new();

    private readonly FakePathProbe _paths = new();

    public void Dispose() => _temp.Dispose();

    private InstalledAppsReader Reader => new(_registry, new FakeWindowsInstaller(), _paths, FixedSystemDirectories.Standard, new FakePackageDependencies(), new FakeVolumeInventory());

    private InstalledEntry Installed(UninstallScope scope, params (string Name, object Value)[] values)
    {
        var uninstaller = Path.Combine(_temp.Path, "Tool", "unins000.exe");
        _paths.File(uninstaller);
        return Reader.ReadAgain(_registry.With(scope, "Tool", [("DisplayName", "Tool"), ("UninstallString", $"\"{uninstaller}\""), .. values]))!;
    }

    private InstalledEntry Stale(UninstallScope scope, params (string Name, object Value)[] values) =>
        Reader.ReadAgain(_registry.With(scope, "Gone",
            [("DisplayName", "Gone"), ("UninstallString", $"\"{Path.Combine(_temp.Path, "Gone", "unins000.exe")}\""), .. values]))!;

    [Fact]
    public void TheStandingBadgeCarriesTheReason()
    {
        var entry = Stale(UninstallScope.CurrentUser);

        var marks = EntryMarks.For(entry, Refused, isElevated: false);

        Assert.Equal("Gone", marks.Standing);
        Assert.Equal(entry.Standing.Reason, marks.Reason);
        Assert.Contains("unins000.exe", marks.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstalledProgramIsPresent() =>
        Assert.Equal("Present", EntryMarks.For(Installed(UninstallScope.CurrentUser), Allowed, isElevated: false).Standing);

    [Fact]
    public void AHiddenEntrySaysWhyWindowsHidesIt()
    {
        var marks = EntryMarks.For(Installed(UninstallScope.CurrentUser, ("SystemComponent", 1)), Allowed, isElevated: false);

        Assert.Equal(EntryListing.WhyHidden(EntryVisibility.SystemComponent), marks.Hidden);
    }

    [Fact]
    public void AListedEntryHasNoHiddenBadge() =>
        Assert.Null(EntryMarks.For(Installed(UninstallScope.CurrentUser), Allowed, isElevated: false).Hidden);

    [Fact]
    public void AMachineWideStaleEntryWearsTheShieldWhenNotElevated()
    {
        var entry = Stale(UninstallScope.Machine64);

        Assert.Equal(EntryRemovalPolicy.MayRemove(entry, isElevated: false).Reason, EntryMarks.For(entry, Refused, isElevated: false).Shield);
        Assert.Null(EntryMarks.For(entry, Refused, isElevated: true).Shield);
    }

    [Fact]
    public void APerUserStaleEntryNeedsNoShield() =>
        Assert.Null(EntryMarks.For(Stale(UninstallScope.CurrentUser), Refused, isElevated: false).Shield);

    [Fact]
    public void AMachineWideProgramWarnsOfTheAdministratorPrompt()
    {
        var entry = Installed(UninstallScope.Machine32);

        Assert.Contains("administrator rights", EntryMarks.For(entry, Allowed, isElevated: false).Shield, StringComparison.Ordinal);
        Assert.Null(EntryMarks.For(entry, Allowed, isElevated: true).Shield);
    }

    /// <summary>Elevating cannot help a program Deguffer will not uninstall, so no shield says it might.</summary>
    [Fact]
    public void AProgramThatCannotBeUninstalledWearsNoShield() =>
        Assert.Null(EntryMarks.For(Installed(UninstallScope.Machine64), Refused, isElevated: false).Shield);

    /// <summary>§7.3: a program that will not be uninstalled says why on its own row.</summary>
    [Fact]
    public void ARefusedProgramCarriesItsRefusal()
    {
        Assert.Equal("No.", EntryMarks.For(Installed(UninstallScope.CurrentUser), Refused, isElevated: false).Refusal);
        Assert.Null(EntryMarks.For(Installed(UninstallScope.CurrentUser), Allowed, isElevated: false).Refusal);
    }

    /// <summary>A stale entry's uninstall is always refused, and saying so on its row would only repeat its standing.</summary>
    [Fact]
    public void AStaleEntryCarriesNoUninstallRefusal() =>
        Assert.Null(EntryMarks.For(Stale(UninstallScope.CurrentUser), Refused, isElevated: false).Refusal);

    [Fact]
    public void APerUserProgramWearsNoShield() =>
        Assert.Null(EntryMarks.For(Installed(UninstallScope.CurrentUser), Allowed, isElevated: false).Shield);

    [Fact]
    public void TheSizeIsTheInstallersEstimateInKilobytes() =>
        Assert.Equal(2048L * 1024, EntryMarks.For(Installed(UninstallScope.CurrentUser, ("EstimatedSize", 2048)), Allowed, false).Size);

    [Fact]
    public void ASizeWrittenAsAQwordIsRead() =>
        Assert.Equal(5L * 1024, EntryMarks.For(Installed(UninstallScope.CurrentUser, ("EstimatedSize", 5L)), Allowed, false).Size);

    /// <summary>Past 2 TB a REG_DWORD reads back negative, and Windows reads it unsigned.</summary>
    [Fact]
    public void ADwordPastTheSignBitIsReadUnsigned() =>
        Assert.Equal(3_000_000_000L * 1024, EntryMarks.For(Installed(UninstallScope.CurrentUser, ("EstimatedSize", unchecked((int)3_000_000_000u))), Allowed, false).Size);

    [Theory]
    [InlineData("large")]
    [InlineData(0)]
    public void ASizeThatSaysNothingIsNotShown(object value) =>
        Assert.Null(EntryMarks.For(Installed(UninstallScope.CurrentUser, ("EstimatedSize", value)), Allowed, false).Size);

    [Fact]
    public void AStaleEntryShowsNoSize() =>
        Assert.Null(EntryMarks.For(Stale(UninstallScope.CurrentUser, ("EstimatedSize", 2048)), Refused, false).Size);
}
