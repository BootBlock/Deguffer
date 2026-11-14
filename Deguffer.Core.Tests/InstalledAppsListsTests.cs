using Deguffer.Core.InstalledApps;
using Deguffer.Core.Viewing;

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
        var lists = InstalledAppsLists.From(new InstalledAppsReading(
            [Entry("b", EntryStanding.Stale), Entry("C"), Entry("a", EntryStanding.Stale), Entry("A2", EntryStanding.Unproven)], []),
            showHidden: false,
            filter: null);

        Assert.Equal(["a", "b"], lists.Stale.Select(e => e.Name));
        Assert.Equal(["A2", "C"], lists.Installed.Select(e => e.Name));
    }

    [Fact]
    public void AHiddenEntryIsShownOnlyOnRequest()
    {
        var entries = new InstalledAppsReading([Entry("Hidden", EntryStanding.Stale, EntryVisibility.SystemComponent)], []);

        Assert.Empty(InstalledAppsLists.From(entries, showHidden: false, filter: null).Stale);
        Assert.Single(InstalledAppsLists.From(entries, showHidden: true, filter: null).Stale);
    }

    [Fact]
    public void AStaleListWithRowsHasNoEmptyNote()
    {
        var reading = new InstalledAppsReading([Entry("Gone", EntryStanding.Stale)], []);

        Assert.Null(InstalledAppsLists.From(reading, showHidden: false, filter: null).StaleEmpty);
    }

    [Fact]
    public void AReadingWithNothingStaleSaysThereIsNone()
    {
        var reading = new InstalledAppsReading([Entry("Tool"), Entry("Maybe", EntryStanding.Unproven)], []);

        var note = InstalledAppsLists.From(reading, showHidden: true, filter: null).StaleEmpty;

        Assert.Equal(new EmptyListNote(
            EmptyListCause.Nothing,
            "No stale entries",
            "Every entry belongs to a program that is still there, or that nothing proves gone."), note);
    }

    /// <summary>A key Windows would not open may hold stale entries, so "none" is said of what was read.</summary>
    [Fact]
    public void NothingStaleNamesAKeyWindowsWouldNotOpen()
    {
        var reading = new InstalledAppsReading([Entry("Tool")], [UninstallScope.Machine32]);

        var note = InstalledAppsLists.From(reading, showHidden: false, filter: null).StaleEmpty!;

        Assert.Equal(EmptyListCause.Nothing, note.Cause);
        Assert.StartsWith("Every entry Deguffer could read", note.Message, StringComparison.Ordinal);
        Assert.Contains(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleEntryTheFilterExcludesIsNotSaidToBeAbsent()
    {
        var reading = new InstalledAppsReading([Entry("Gone", EntryStanding.Stale), Entry("Tool")], []);

        var note = InstalledAppsLists.From(reading, showHidden: false, filter: "tool").StaleEmpty!;

        Assert.Equal(EmptyListCause.Filtered, note.Cause);
        Assert.Equal("No stale entry matches the filter", note.Title);
    }

    [Fact]
    public void AHiddenStaleEntryIsCountedAndTheSwitchNamed()
    {
        var reading = new InstalledAppsReading(
            [Entry("One", EntryStanding.Stale, EntryVisibility.SystemComponent), Entry("Two", EntryStanding.Stale, EntryVisibility.NoName)],
            []);

        var note = InstalledAppsLists.From(reading, showHidden: false, filter: null).StaleEmpty;

        Assert.Equal(new EmptyListNote(
            EmptyListCause.Hidden,
            "No stale entries in the list Windows shows",
            "2 stale entries are hidden. Turn on Show entries Windows hides to list hidden entries too."), note);
    }

    /// <summary>
    /// Only a hidden entry the filter matches is one the switch would list. A hidden stale entry the
    /// filter excludes leaves the filter as the cause.
    /// </summary>
    [Fact]
    public void AHiddenStaleEntryTheFilterExcludesNamesTheFilter()
    {
        var reading = new InstalledAppsReading([Entry("Gone", EntryStanding.Stale, EntryVisibility.SystemComponent), Entry("Tool")], []);

        Assert.Equal(EmptyListCause.Filtered, InstalledAppsLists.From(reading, showHidden: false, filter: "tool").StaleEmpty!.Cause);
        Assert.Equal(EmptyListCause.Hidden, InstalledAppsLists.From(reading, showHidden: false, filter: "gone").StaleEmpty!.Cause);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("example")]
    [InlineData("TOOL_KEY")]
    [InlineData("remove-Tool")]
    public void TheFilterMatchesNamePublisherKeyAndCommand(string filter)
    {
        var entries = new InstalledAppsReading([Entry("Tool", publisher: "Example Ltd"), Entry("Other")], []);

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
        Assert.StartsWith("'Machine' will be left.", selection.Note, StringComparison.Ordinal);
    }

    /// <summary>Worded as the installed list words it: with nothing else selected, the refusal alone.</summary>
    [Fact]
    public void ALoneRefusedSelectionSaysTheRefusalAlone()
    {
        var machine = Entry("Machine", EntryStanding.Stale, scope: UninstallScope.Machine64);

        var selection = RemovalSelection.For([machine], isElevated: false);

        Assert.Equal(EntryRemovalPolicy.MayRemove(machine, isElevated: false).Reason, selection.Note);
    }

    [Fact]
    public void ASelectionThatMayAllGoHasNoNote()
    {
        var selection = RemovalSelection.For([Entry("Mine", EntryStanding.Stale)], isElevated: false);

        Assert.Null(selection.Note);
    }
}
