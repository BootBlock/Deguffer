using Deguffer.Core.Startup;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.5 lists the Startup page with the entries that start first unless the user chose another order,
/// and never by the memory a program uses.
/// </summary>
public sealed class StartupRowOrderTests
{
    private static readonly StartupEntry Zulu = Entry("Zulu", StartupState.Starts, StartupSource.UserRun, "Contoso");
    private static readonly StartupEntry Alpha = Entry("Alpha", StartupState.Disabled, StartupSource.PackagedTask, null);
    private static readonly StartupEntry Mike = Entry("Mike", StartupState.Unknown, StartupSource.MachineRun, "Fabrikam");
    private static readonly StartupEntry Bravo = Entry("Bravo", StartupState.Starts, StartupSource.UserStartupFolder, null);
    private static readonly StartupEntry Echo = Entry("Echo", StartupState.Disabled, StartupSource.UserRun, "Contoso");

    private static readonly StartupEntry[] All = [Zulu, Alpha, Mike, Bravo, Echo];

    [Theory]
    [InlineData(StartupOrder.Name, StartupOrderDirection.Ascending, "Alpha,Bravo,Echo,Mike,Zulu")]
    [InlineData(StartupOrder.Name, StartupOrderDirection.Descending, "Zulu,Mike,Echo,Bravo,Alpha")]
    [InlineData(StartupOrder.State, StartupOrderDirection.Ascending, "Bravo,Zulu,Alpha,Echo,Mike")]
    [InlineData(StartupOrder.State, StartupOrderDirection.Descending, "Mike,Alpha,Echo,Bravo,Zulu")]
    [InlineData(StartupOrder.Source, StartupOrderDirection.Ascending, "Echo,Zulu,Mike,Bravo,Alpha")]
    [InlineData(StartupOrder.Source, StartupOrderDirection.Descending, "Alpha,Bravo,Mike,Echo,Zulu")]
    public void EachOrderRunsEitherWayRoundWithTiesByName(StartupOrder order, StartupOrderDirection direction, string expected) =>
        Assert.Equal(expected, Names(order, direction));

    /// <summary>
    /// Reversed, the publishers run Z to A, and the entries nobody could name a maker for stay last
    /// rather than opening the list.
    /// </summary>
    [Fact]
    public void AnEntryWithNoKnownPublisherStandsLastEitherWayRound()
    {
        Assert.Equal("Echo,Zulu,Mike,Alpha,Bravo", Names(StartupOrder.Publisher, StartupOrderDirection.Ascending));
        Assert.Equal("Mike,Echo,Zulu,Alpha,Bravo", Names(StartupOrder.Publisher, StartupOrderDirection.Descending));
    }

    /// <summary>A tie in name goes by source, in the order the page reads the sources, whichever way round the names run.</summary>
    [Fact]
    public void TwoEntriesOfOneTitleGoBySourceAsTheReadingPutsThem()
    {
        var machine = Entry("Same", StartupState.Starts, StartupSource.MachineRun, null);
        var user = Entry("Same", StartupState.Starts, StartupSource.UserRun, null);

        Assert.Equal([user, machine], new[] { machine, user }.Order(StartupRowOrder.Comparer(StartupOrder.Name, StartupOrderDirection.Ascending)));
        Assert.Equal([user, machine], new[] { machine, user }.Order(StartupRowOrder.Comparer(StartupOrder.Name, StartupOrderDirection.Descending)));
    }

    /// <summary>
    /// The preferences file allows numbers for an enum, so a hand-edited value no member names arrives
    /// and is shown as the shipped order: the entries that start, then the disabled ones, then the
    /// unknown.
    /// </summary>
    [Fact]
    public void AValueNoOrderIsNamedForIsTheShippedOrder()
    {
        Assert.Equal(StartupOrder.State, StartupRowOrder.Known((StartupOrder)9));
        Assert.Equal(StartupOrderDirection.Ascending, StartupRowOrder.Known((StartupOrderDirection)9));
        Assert.Equal("Bravo,Zulu,Alpha,Echo,Mike", Names((StartupOrder)9, (StartupOrderDirection)9));
    }

    /// <summary>§2: nothing on the page is ordered by what its program costs, so no order is named for memory.</summary>
    [Fact]
    public void NoOrderIsByMemory() =>
        Assert.Equal(
            [StartupOrder.Name, StartupOrder.State, StartupOrder.Source, StartupOrder.Publisher],
            Enum.GetValues<StartupOrder>());

    private static string Names(StartupOrder order, StartupOrderDirection direction) =>
        string.Join(",", All.Order(StartupRowOrder.Comparer(order, direction)).Select(e => e.Title));

    private static StartupEntry Entry(string title, StartupState state, StartupSource source, string? publisher) =>
        new(new StartupKey(source, title), title, Command: null, Program: null, ProgramStanding.Unknown, publisher, state, Approval: null);
}
