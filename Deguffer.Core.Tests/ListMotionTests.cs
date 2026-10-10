using Deguffer.Core.Viewing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What each row on screen does as its list changes: a row leaving fades where it stood while the
/// rows below glide up, a row arriving pushes the rows below down, a row the sort moved glides to
/// its place, a change that replaces most of the list crossfades, and the row the reader is at keeps
/// its place unless it is the one that changed.
/// </summary>
public sealed class ListMotionTests
{
    private const double Row = 40;

    private static readonly Motion Full = MotionToken.List.Full;

    [Fact]
    public void ARowLeavingFadesWhereItStoodAndTheRowsBelowGlideUp()
    {
        var plan = Plan(Tops("a", "b", "c", "d"), Placed("a", "c", "d"), departed: ["b"]);

        Assert.False(plan.Crossfade);
        Assert.Equal(
            [Leave("b", Row), Glide("c", Row), Glide("d", Row)],
            plan.Rows);
    }

    [Fact]
    public void ARowArrivingPushesTheRowsBelowDown()
    {
        var plan = Plan(Tops("a", "b", "c"), Placed("a", "new", "b", "c"), arrived: ["new"]);

        Assert.False(plan.Crossfade);
        Assert.Equal(
            [Arrive("new"), Glide("b", -Row), Glide("c", -Row)],
            plan.Rows);
    }

    [Fact]
    public void ARowTheSortMovedGlidesToItsPlaceAndTheRowsItPassedGlideTheOtherWay()
    {
        var plan = Plan(Tops("a", "b", "c", "d"), Placed("c", "a", "b", "d"));

        Assert.Equal(
            [Glide("c", 2 * Row), Glide("a", -Row), Glide("b", -Row)],
            plan.Rows);
    }

    [Fact]
    public void ARowScrolledIntoViewGoesWithTheRowAboveItRatherThanAppearingInPlace()
    {
        // "e" was below the window before the change, so nobody saw it: it comes up with "d".
        var plan = Plan(Tops("a", "b", "c", "d"), Placed("b", "c", "d", "e"), departed: ["a"]);

        Assert.Contains(Glide("e", Row), plan.Rows);
        Assert.Contains(Glide("d", Row), plan.Rows);
    }

    [Fact]
    public void ARowScrolledInAboveEveryRowSeenGoesWithTheFirstRowBelowIt()
    {
        // The list kept a row lower down in place by scrolling up: "z" came in from above.
        var before = new Dictionary<string, double>(StringComparer.Ordinal) { ["a"] = 0, ["b"] = Row };
        var after = new List<(string, double)> { ("z", 0), ("a", Row), ("b", 2 * Row) };

        var plan = ListMotion.Plan(before, after, Set(), Set(), Full, StringComparer.Ordinal);

        Assert.Equal([Glide("z", -Row), Glide("a", -Row), Glide("b", -Row)], plan.Rows);
    }

    [Fact]
    public void ANewRowIsNotMistakenForOneScrolledIn()
    {
        var plan = Plan(Tops("a", "b", "c", "d"), Placed("b", "c", "d", "new"), arrived: ["new"], departed: ["a"]);

        Assert.Contains(Arrive("new"), plan.Rows);
        Assert.DoesNotContain(plan.Rows, row => row.Key == "new" && row.Move == RowMove.Glide);
    }

    [Fact]
    public void WithMotionOffNothingGlidesButRowsStillFadeInAndOut()
    {
        // With motion on, the rows below each change would glide.
        var arriving = Plan(Tops("a", "b", "c", "d"), Placed("a", "new", "b", "c", "d"), arrived: ["new"], played: MotionToken.List.Reduced);
        var leaving = Plan(Tops("a", "b", "c", "d"), Placed("a", "c", "d"), departed: ["b"], played: MotionToken.List.Reduced);

        Assert.Equal([Arrive("new")], arriving.Rows);
        Assert.Equal([Leave("b", Row)], leaving.Rows);
    }

    [Fact]
    public void AnInstantMotionMovesNothing()
    {
        var plan = Plan(Tops("a", "b"), Placed("b"), departed: ["a"], played: Motion.Instant);

        Assert.Same(ListMotionPlan<string>.Still, plan);
    }

    [Fact]
    public void AChangeThatReplacesMostOfTheRowsCrossfadesAndSparesARowThatStayedPut()
    {
        var plan = Plan(
            Tops("a", "b", "c", "d"),
            Placed("a", "w", "x", "y"),
            arrived: ["w", "x", "y"],
            departed: ["b", "c", "d"]);

        Assert.True(plan.Crossfade);
        Assert.Equal(
            [Leave("b", Row), Leave("c", 2 * Row), Leave("d", 3 * Row), Arrive("w"), Arrive("x"), Arrive("y")],
            plan.Rows);
    }

    [Fact]
    public void AListFilledFromEmptyFadesInAsOnePicture()
    {
        var plan = Plan(Tops(), Placed("a", "b", "c"), arrived: ["a", "b", "c"]);

        Assert.True(plan.Crossfade);
        Assert.Equal([Arrive("a"), Arrive("b"), Arrive("c")], plan.Rows);
    }

    [Fact]
    public void TheRowUnderThePointerKeepsItsPlaceWhenARowAboveItLeaves()
    {
        var shift = ListMotion.Hold(["c"], Tops("a", "b", "c", "d"), Placed("a", "c", "d"), StringComparer.Ordinal);

        Assert.Equal(-Row, shift);
    }

    [Fact]
    public void ARowTheSortMovedIsNotHeldInPlace()
    {
        var shift = ListMotion.Hold(["c"], Tops("a", "b", "c", "d"), Placed("c", "a", "b", "d"), StringComparer.Ordinal);

        Assert.Null(shift);
    }

    [Fact]
    public void TheFocusedRowIsHeldWhenTheRowUnderThePointerWasTheOneThatMoved()
    {
        // "d" sank below "e" and is passed over, so the focused "b" is held: "a" left above it.
        var shift = ListMotion.Hold(
            ["d", "b"],
            Tops("a", "b", "c", "d", "e"),
            Placed("b", "c", "e", "d"),
            StringComparer.Ordinal);

        Assert.Equal(-Row, shift);
    }

    [Fact]
    public void ARowThatDidNotMoveNeedsNoScroll()
    {
        var shift = ListMotion.Hold(["a"], Tops("a", "b", "c"), Placed("a", "c"), StringComparer.Ordinal);

        Assert.Null(shift);
    }

    private static ListMotionPlan<string> Plan(
        Dictionary<string, double> before,
        List<(string, double)> after,
        string[]? arrived = null,
        string[]? departed = null,
        Motion? played = null) =>
        ListMotion.Plan(before, after, Set(arrived ?? []), Set(departed ?? []), played ?? Full, StringComparer.Ordinal);

    /// <summary>Rows one under another from the top, each a row high, keyed by their top.</summary>
    private static Dictionary<string, double> Tops(params string[] keys) =>
        keys.Select((key, at) => (key, at)).ToDictionary(row => row.key, row => row.at * Row, StringComparer.Ordinal);

    private static List<(string, double)> Placed(params string[] keys) =>
        [.. keys.Select((key, at) => (key, at * Row))];

    private static HashSet<string> Set(params string[] keys) => new(keys, StringComparer.Ordinal);

    private static RowMotion<string> Leave(string key, double top) => new(key, RowMove.Leave, top);

    private static RowMotion<string> Arrive(string key) => new(key, RowMove.Arrive, 0);

    private static RowMotion<string> Glide(string key, double from) => new(key, RowMove.Glide, from);
}
