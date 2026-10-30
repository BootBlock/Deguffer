using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.1 and §7.2: Explore and Memory never pre-select. A selection a list reports is the reader's only
/// where the reader touched the list since it last settled on rows it was given, and a press held on a
/// row through a rewrite keeps its click.
/// </summary>
public sealed class ListGesturesTests
{
    private static readonly ListGesture[] Every = Enum.GetValues<ListGesture>();

    [Fact]
    public void AListNobodyHasTouchedHasItsOwnHighlightPutBack() =>
        Assert.Equal(ListReport.Restore, ListGesture.Settled.Judge(writing: false));

    [Fact]
    public void AKeyMakesTheNextReportTheReaders()
    {
        var gesture = ListGesture.Settled.Keyed();

        Assert.Equal(ListGesture.Touched, gesture);
        Assert.Equal(ListReport.Accept, gesture.Judge(writing: false));
    }

    [Fact]
    public void AClickOnARowIsTheReadersOnTheFirstPressAfterARewrite()
    {
        var gesture = ListGestures.Pressed(onRow: true).Released();

        Assert.Equal(ListGesture.Touched, gesture);
        Assert.Equal(ListReport.Accept, gesture.Judge(writing: false));
    }

    /// <summary>A report arriving while the page or the view-model writes the rows is that write's, whoever touched what.</summary>
    [Fact]
    public void AReportDuringAWriteIsIgnoredWhateverTheReaderDid()
    {
        foreach (var gesture in Every)
        {
            Assert.Equal(ListReport.Ignore, gesture.Judge(writing: true));
        }
    }

    [Fact]
    public void ASettleForgetsATouch()
    {
        var gesture = ListGesture.Settled.Keyed().Settle();

        Assert.Equal(ListGesture.Settled, gesture);
        Assert.Equal(ListReport.Restore, gesture.Judge(writing: false));
    }

    /// <summary>
    /// A list commits a pointer selection on the release, so a rewrite landing while the press is held
    /// would otherwise refuse the click it was about to report.
    /// </summary>
    [Fact]
    public void ARewriteDuringAPressOnARowKeepsTheClick()
    {
        var gesture = ListGestures.Pressed(onRow: true).Settle();

        Assert.Equal(ListGesture.Pressing, gesture);

        gesture = gesture.Released();

        Assert.Equal(ListReport.Accept, gesture.Judge(writing: false));
    }

    /// <summary>
    /// A press on the list's background or its scrollbar commits no selection, and may end somewhere
    /// that raises nothing on the list. Holding the guard open for it would admit the write §7.1
    /// forbids for as long as a scrollbar drag lasts.
    /// </summary>
    [Fact]
    public void APressBesideTheRowsDoesNotHoldOffASettle()
    {
        var gesture = ListGestures.Pressed(onRow: false);

        Assert.Equal(ListGesture.Touched, gesture);
        Assert.Equal(ListGesture.Settled, gesture.Settle());
    }

    [Fact]
    public void AKeyDuringAPressOnARowLeavesThePressHeld() =>
        Assert.Equal(ListGesture.Pressing, ListGesture.Pressing.Keyed());

    [Fact]
    public void AnEndWithoutAPressChangesNothing()
    {
        Assert.Equal(ListGesture.Settled, ListGesture.Settled.Released());
        Assert.Equal(ListGesture.Touched, ListGesture.Touched.Released());
    }

    /// <summary>A press can land between the control's report and the restore, and the restore would then drop it.</summary>
    [Fact]
    public void ARestoreRunsOnlyOnAListStillUntouched()
    {
        Assert.True(ListGesture.Settled.StillUntouched());
        Assert.False(ListGesture.Touched.StillUntouched());
        Assert.False(ListGesture.Pressing.StillUntouched());
    }
}
