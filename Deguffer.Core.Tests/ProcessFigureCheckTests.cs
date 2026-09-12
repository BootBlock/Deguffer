using Deguffer.Core.Memory;

namespace Deguffer.Core.Tests;

/// <summary>
/// The two undocumented figures are believed only where Deguffer's own record agrees with what Windows
/// documents about Deguffer's own process (§7.2). These prove each way of disagreeing turns them off,
/// and that ordinary drift between two readings of a moving figure does not.
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
    /// Twenty megabytes apart on a hundred is a fifth, inside the quarter two readings may drift, and
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

    [Fact]
    public void A32BitProcessOn64BitWindowsTurnsThemOffHoweverWellTheyAgree() =>
        Assert.Equal(
            ProcessFigures.NotOnThisArchitecture,
            ProcessFigureCheck.Judge(
                TableWithOwn(privateWorkingSet: 100 * MiB),
                Own,
                new OwnProcessReference(Created, 100 * MiB),
                narrowOnWideWindows: true));

    private static ProcessFigures Judge(ParsedProcessTable table, OwnProcessReference reference) =>
        ProcessFigureCheck.Judge(table, Own, reference, narrowOnWideWindows: false);

    private static ParsedProcessTable TableWithOwn(long privateWorkingSet) =>
        new([Record(Own - 8, 900 * MiB), Record(Own, privateWorkingSet)], Complete: true);

    private static ProcessRecord Record(int processId, long privateWorkingSet) =>
        new(processId, ParentProcessId: 4, Name: "alpha.exe", CommitCharge: 2 * privateWorkingSet, privateWorkingSet, Created);
}
