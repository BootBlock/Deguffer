using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which part of a change of folder's flight is under way: the middle of a flight through a folder is
/// reached once, a flight turned round goes back between the ends of the leg it was on, and a flight
/// turned round on its way out never goes on into the folder no longer asked for.
/// </summary>
public sealed class MapCourseTests
{
    private static readonly MapFrame Left = new(0.1, 0.2, 0.5, 0.5);

    private static readonly MapFrame Going = new(0.7, 0.75, 0.125, 0.125);

    private static readonly MapPassage Passage = new(new MapDescent(Left, true), new MapDescent(Going, true));

    [Fact]
    public void AFlightIntoOrOutOfAFolderIsOnItsOneDescentAndTurnsBackToTheOtherEnd()
    {
        var path = new MapDescent(Going, true);
        var course = MapCourse.Descent(path);

        Assert.Equal((path, 0.3), course.At(0.3));
        Assert.Equal(0, course.Turned(1).Back);
        Assert.Equal(1, course.Turned(0).Back);
        Assert.False(course.Reached(1).Midway);
    }

    /// <summary>The map swaps the pictures over at the middle, so it hears of it once, at the progress that reaches it.</summary>
    [Fact]
    public void AFlightThroughAFolderReachesTheMiddleOnce()
    {
        var course = MapCourse.Through(Passage);

        var (before, early) = course.Reached(Passage.Midway - 1e-9);
        var (past, midway) = before.Reached(Passage.Midway);
        var (_, again) = past.Reached(Passage.Midway + 0.1);

        Assert.False(early);
        Assert.True(midway);
        Assert.False(again);
        Assert.Equal(Passage.At(0.9), past.At(0.9));
    }

    /// <summary>
    /// Turned round on its way out, the flight goes back to the folder it left. The spring carries it
    /// on a little first, and can carry it past the middle: it holds there rather than reporting a
    /// middle that would swap in a picture nobody asked for, or flying on into it.
    /// </summary>
    [Fact]
    public void AFlightTurnedOnItsWayOutGoesBackToTheFolderLeftAndNeverReachesTheMiddle()
    {
        var (turned, back) = MapCourse.Through(Passage).Turned(1);
        var beyond = Passage.Midway + 0.1;

        Assert.Equal(0, back);
        Assert.False(turned.Reached(beyond).Midway);
        Assert.Equal((Passage.Out, 0.0), turned.At(beyond));
        Assert.Equal(Passage.At(0.1), turned.At(0.1));
    }

    /// <summary>Turned round past the middle, the flight goes back only as far as the middle picture, the folder passed through.</summary>
    [Fact]
    public void AFlightTurnedPastTheMiddleGoesBackToTheMiddlePicture()
    {
        var (past, _) = MapCourse.Through(Passage).Reached(0.9);
        var (turned, back) = past.Turned(1);

        Assert.Equal(Passage.Midway, back);
        Assert.Equal(Passage.At(Passage.Midway), turned.At(Passage.Midway));
    }

    /// <summary>
    /// Turned round twice, the flight goes back the way it was going within its leg: on the way out, to
    /// the middle picture without going on past it, and on the way in, to the folder going to again.
    /// </summary>
    [Fact]
    public void AFlightTurnedTwiceGoesOnWithinItsLeg()
    {
        var (outward, toLeft) = MapCourse.Through(Passage).Turned(1);
        var (onward, toMiddle) = outward.Turned(toLeft);

        Assert.Equal(Passage.Midway, toMiddle);
        Assert.False(onward.Reached(Passage.Midway).Midway);
        Assert.Equal((Passage.Out, 0.0), onward.At(Passage.Midway + 0.1));

        var (past, _) = MapCourse.Through(Passage).Reached(0.9);
        var (inward, toPassed) = past.Turned(1);

        Assert.Equal(1, inward.Turned(toPassed).Back);
    }

    /// <summary>The way out held at the middle is the way out itself before it, and the folder left filling the screen before the start.</summary>
    [Fact]
    public void TheWayOutHeldAtTheMiddleIsTheWayOutBeforeIt()
    {
        Assert.Equal(Passage.At(0.2), Passage.OutAt(0.2));
        Assert.Equal((Passage.Out, 0.0), Passage.OutAt(Passage.Midway + 0.2));
        Assert.Equal((Passage.Out, 1.0), Passage.OutAt(-0.1));
    }
}
