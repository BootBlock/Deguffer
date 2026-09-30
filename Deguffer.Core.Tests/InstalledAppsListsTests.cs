using Deguffer.Core.InstalledApps;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the page's two lists hold (§7.3), and what a selection may remove. Decisions the page makes
/// on the user's behalf, so they are proved here rather than in the shell.
/// </summary>
public sealed class InstalledAppsListsTests
{
    private static InstalledEntry Entry(
        string name,
        EntryStanding standing = EntryStanding.Installed,
        EntryVisibility visibility = EntryVisibility.Listed,
        UninstallScope scope = UninstallScope.CurrentUser,
        string? publisher = null) => new(
            new UninstallKey(scope, name + "_key"),
            name,
            publisher,
            Version: null,
            UninstallValues.None,
            visibility,
            new UnparsedCommand($"remove-{name}"),
            new StandingVerdict(standing, $"Because of {name}."),
            NoRemove: false);

    [Fact]
    public void StaleAndInstalledEntriesGoToTheirOwnListsInNameOrder()
    {
        var lists = InstalledAppsLists.From(
            [Entry("b", EntryStanding.Stale), Entry("C"), Entry("a", EntryStanding.Stale), Entry("A2", EntryStanding.Unproven)],
            showHidden: false,
            filter: null);

        Assert.Equal(["a", "b"], lists.Stale.Select(e => e.Name));
        Assert.Equal(["A2", "C"], lists.Installed.Select(e => e.Name));
    }

    [Fact]
    public void AHiddenEntryIsShownOnlyOnRequest()
    {
        IReadOnlyList<InstalledEntry> entries = [Entry("Hidden", EntryStanding.Stale, EntryVisibility.SystemComponent)];

        Assert.Empty(InstalledAppsLists.From(entries, showHidden: false, filter: null).Stale);
        Assert.Single(InstalledAppsLists.From(entries, showHidden: true, filter: null).Stale);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("example")]
    [InlineData("TOOL_KEY")]
    [InlineData("remove-Tool")]
    public void TheFilterMatchesNamePublisherKeyAndCommand(string filter)
    {
        IReadOnlyList<InstalledEntry> entries = [Entry("Tool", publisher: "Example Ltd"), Entry("Other")];

        Assert.Equal("Tool", Assert.Single(InstalledAppsLists.From(entries, showHidden: false, filter).Installed).Name);
    }

    [Fact]
    public void TheHeadlineCountsWhatWindowsListsAndWhatIsHidden()
    {
        var reading = new InstalledAppsReading(
            [Entry("a", EntryStanding.Stale), Entry("b"), Entry("c"), Entry("d", EntryStanding.Stale, EntryVisibility.NoName)],
            []);

        Assert.Equal(
            "1 stale entry and 2 installed programs in the list Windows shows. 1 more entry is hidden, 1 of them stale.",
            InstalledAppsLists.Headline(reading));
    }

    [Fact]
    public void TheHeadlineNamesAKeyWindowsWouldNotOpen()
    {
        var reading = new InstalledAppsReading([], [UninstallScope.Machine32]);

        Assert.Contains(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node", InstalledAppsLists.Headline(reading), StringComparison.Ordinal);
    }

    [Fact]
    public void ASelectionRemovesWhatItMayAndSaysWhyTheRestIsLeft()
    {
        var mine = Entry("Mine", EntryStanding.Stale);
        var machine = Entry("Machine", EntryStanding.Stale, scope: UninstallScope.Machine64);

        var selection = RemovalSelection.For([mine, machine], isElevated: false);

        Assert.Equal([mine], selection.Removable);
        Assert.True(selection.NeedsElevation);
        Assert.StartsWith("'Machine' will be left.", selection.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ASelectionThatMayAllGoHasNoNote()
    {
        var selection = RemovalSelection.For([Entry("Mine", EntryStanding.Stale)], isElevated: false);

        Assert.Null(selection.Note);
        Assert.False(selection.NeedsElevation);
    }
}
