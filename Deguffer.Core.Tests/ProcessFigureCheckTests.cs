using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests;

/// <summary>
/// The two undocumented figures are believed only where Deguffer's own record agrees with what Windows
/// documents about Deguffer's own process (§7.2). These prove each way of disagreeing turns them off,
/// that ordinary drift between the table and the counter does not, and that only the verdicts which
/// cannot change are kept.
/// </summary>
public sealed class ProcessFigureCheckTests
{
    private const int Own = 7_000;
    private const long Created = 133_900_000_000_000_042;
    private const long MiB = 1024 * 1024;

    [Fact]
    public void FiguresThatAgreeWithTheOwnProcessAreChecked() =>
        Assert.Equal(
            ProcessFigures.Checked,
            Judge(TableWithOwn(privateWorkingSet: 100 * MiB), new OwnProcessReference(Created, 101 * MiB)));

    [Fact]
    public void ADifferentCreationTimeTurnsThemOff() =>
        Assert.Equal(
            ProcessFigures.CreationTimeDisagrees,
            Judge(TableWithOwn(privateWorkingSet: 100 * MiB), new OwnProcessReference(Created + 1, 100 * MiB)));

    [Fact]
    public void APrivateWorkingSetFarFromTheCounterTurnsThemOff() =>
        Assert.Equal(
            ProcessFigures.PrivateWorkingSetDisagrees,
            Judge(TableWithOwn(privateWorkingSet: 100 * MiB), new OwnProcessReference(Created, 400 * MiB)));

    /// <summary>
    /// Twenty megabytes apart on a hundred is a fifth, inside the quarter the two may drift, and
    /// outside anything tighter.
    /// </summary>
    [Fact]
    public void DriftWithinAQuarterOfTheCounterStillAgrees() =>
        Assert.Equal(
            ProcessFigures.Checked,
            Judge(TableWithOwn(privateWorkingSet: 120 * MiB), new OwnProcessReference(Created, 100 * MiB)));

    /// <summary>
    /// A quarter of four megabytes is one, which a single garbage collection crosses, so a small
    /// process is held to the floor instead: fourteen apart agrees and twenty does not.
    /// </summary>
    [Theory]
    [InlineData(18, ProcessFigures.Checked)]
    [InlineData(24, ProcessFigures.PrivateWorkingSetDisagrees)]
    public void ASmallProcessIsJudgedByTheFloor(long readMiB, ProcessFigures expected) =>
        Assert.Equal(
            expected,
            Judge(TableWithOwn(privateWorkingSet: readMiB * MiB), new OwnProcessReference(Created, 4 * MiB)));

    [Fact]
    public void NoDocumentedCounterLeavesNothingToCheckAgainst() =>
        Assert.Equal(
            ProcessFigures.NothingToCheckAgainst,
            Judge(TableWithOwn(privateWorkingSet: 100 * MiB), new OwnProcessReference(Created, null)));

    [Fact]
    public void ATableWithoutTheOwnProcessTurnsThemOff()
    {
        var table = new ParsedProcessTable([Record(Own + 4, 100 * MiB)], Complete: true);

        Assert.Equal(
            ProcessFigures.OwnProcessNotListed,
            Judge(table, new OwnProcessReference(Created, 100 * MiB)));
    }

    /// <summary>
    /// A layout that checked out once belongs to the Windows build, so the second read neither
    /// compares again nor pays to read the counters, even given a table that would fail.
    /// </summary>
    [Fact]
    public void APassIsKeptWithoutReadingTheCountersAgain()
    {
        var check = new ProcessFigureCheck(Own, narrowOnWideWindows: false);

        Assert.Equal(
            ProcessFigures.Checked,
            check.Judge(TableWithOwn(privateWorkingSet: 100 * MiB), () => new OwnProcessReference(Created, 100 * MiB)));

        Assert.Equal(
            ProcessFigures.Checked,
            check.Judge(new ParsedProcessTable([], Complete: true), Unread));
    }

    /// <summary>
    /// A disagreement may be the table and the counter landing apart for a moment, so it is not kept:
    /// the next read that agrees turns the figures on.
    /// </summary>
    [Fact]
    public void AFailureIsCheckedAgainOnTheNextRead()
    {
        var check = new ProcessFigureCheck(Own, narrowOnWideWindows: false);
        var table = TableWithOwn(privateWorkingSet: 100 * MiB);

        Assert.Equal(
            ProcessFigures.PrivateWorkingSetDisagrees,
            check.Judge(table, () => new OwnProcessReference(Created, 400 * MiB)));

        Assert.Equal(
            ProcessFigures.Checked,
            check.Judge(table, () => new OwnProcessReference(Created, 100 * MiB)));
    }

    /// <summary>
    /// However well the figures agree, and on every read: the architecture cannot change while the
    /// process runs, so the counters are never read to learn what is already known.
    /// </summary>
    [Fact]
    public void A32BitProcessOn64BitWindowsTurnsThemOffWithoutReadingTheCounters()
    {
        var check = new ProcessFigureCheck(Own, narrowOnWideWindows: true);
        var table = TableWithOwn(privateWorkingSet: 100 * MiB);

        Assert.Equal(ProcessFigures.NotOnThisArchitecture, check.Judge(table, Unread));
        Assert.Equal(ProcessFigures.NotOnThisArchitecture, check.Judge(table, Unread));
    }

    private static OwnProcessReference Unread() =>
        throw new InvalidOperationException("The counters were read when the verdict was already known.");

    private static ProcessFigures Judge(ParsedProcessTable table, OwnProcessReference reference) =>
        new ProcessFigureCheck(Own, narrowOnWideWindows: false).Judge(table, () => reference);

    private static ParsedProcessTable TableWithOwn(long privateWorkingSet) =>
        new([Record(Own - 8, 900 * MiB), Record(Own, privateWorkingSet)], Complete: true);

    private static ProcessRecord Record(int processId, long privateWorkingSet) =>
        new(processId, ParentProcessId: 4, Name: "alpha.exe", CommitCharge: 2 * privateWorkingSet, privateWorkingSet, Created);
}
