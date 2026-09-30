using Deguffer.Core.InstalledApps;

namespace Deguffer.Core.Tests;

/// <summary>What the page offers to remove (§7.3): a stale entry, with administrator rights where it is machine-wide.</summary>
public sealed class EntryRemovalPolicyTests
{
    private static InstalledEntry Entry(UninstallScope scope, EntryStanding standing) => new(
        new UninstallKey(scope, "Tool"),
        "Tool",
        Publisher: null,
        Version: null,
        UninstallValues.None,
        EntryVisibility.Listed,
        MissingCommand.Instance,
        new StandingVerdict(standing, "Because."),
        NoRemove: false);

    [Theory]
    [InlineData(EntryStanding.Installed)]
    [InlineData(EntryStanding.Unproven)]
    [InlineData(EntryStanding.OtherAccount)]
    public void OnlyAStaleEntryIsRemovable(EntryStanding standing)
    {
        var verdict = EntryRemovalPolicy.MayRemove(Entry(UninstallScope.CurrentUser, standing), isElevated: true);

        Assert.False(verdict.IsAllowed);
        Assert.False(verdict.NeedsElevation);
    }

    [Fact]
    public void AStaleEntryForThisUserNeedsNoElevation()
    {
        Assert.True(EntryRemovalPolicy.MayRemove(Entry(UninstallScope.CurrentUser, EntryStanding.Stale), isElevated: false).IsAllowed);
    }

    [Theory]
    [InlineData(UninstallScope.Machine64)]
    [InlineData(UninstallScope.Machine32)]
    public void AStaleMachineWideEntryNeedsElevation(UninstallScope scope)
    {
        var refused = EntryRemovalPolicy.MayRemove(Entry(scope, EntryStanding.Stale), isElevated: false);

        Assert.False(refused.IsAllowed);
        Assert.True(refused.NeedsElevation);
        Assert.True(EntryRemovalPolicy.MayRemove(Entry(scope, EntryStanding.Stale), isElevated: true).IsAllowed);
    }

    [Fact]
    public void TheConfirmationSaysNothingIsUninstalledAndWhetherABackupIsKept()
    {
        var entries = new[] { Entry(UninstallScope.CurrentUser, EntryStanding.Stale) };

        var withBackup = InstalledAppsPrompt.ForRemoval(entries, backUp: true, @"C:\backups");
        var without = InstalledAppsPrompt.ForRemoval(entries, backUp: false, @"C:\backups");

        Assert.Contains("Nothing is uninstalled and no file is touched", withBackup.Consequence, StringComparison.Ordinal);
        Assert.Contains(@"C:\backups", withBackup.Consequence, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", without.Consequence, StringComparison.Ordinal);
        Assert.Equal(["Tool (This user)"], withBackup.Items);
    }
}
