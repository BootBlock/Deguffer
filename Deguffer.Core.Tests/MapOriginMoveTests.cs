using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The camera moving to a new origin, until the tracker answers. A report read from the wrong origin
/// puts the picture tens of thousands of pixels from where it is, and every click is resolved through
/// what the reports say the screen shows (§7.1).
/// </summary>
public sealed class MapOriginMoveTests
{
    private static readonly MapOriginMove Move = new(
        12,
        default,
        new MapOrigin(0.6, 0.3),
        MapViewport.Seen(64, 0.6, 0.3));

    /// <summary>
    /// The tracker answers in the order it did things, so a report of the move's own request, or of one
    /// asked after it, says the move was carried out; one asked before it does not.
    /// </summary>
    [Fact]
    public void OnlyTheMoveOrALaterRequestCarriesItOut()
    {
        Assert.True(Move.CarriedOutBy(12));
        Assert.True(Move.CarriedOutBy(13), "a request asked after the move was taken as before it");
        Assert.False(Move.CarriedOutBy(11), "a request asked before the move was taken as carrying it out");
    }

    /// <summary>
    /// A hand's report says nothing of whether the move will be carried out: a coast on the wheel or
    /// the touchpad lets the tracker carry it out after the hand's report. Only the tracker refusing
    /// the move's own request means it never moved.
    /// </summary>
    [Fact]
    public void AHandNeitherCarriesTheMoveOutNorRefusesIt()
    {
        Assert.False(Move.CarriedOutBy(0), "a hand's report was taken as the move carried out");
        Assert.False(Move.RefusedBy(0), "a hand's report was taken as the move refused");
        Assert.False(Move.RefusedBy(11), "a refusal of an earlier request was taken as refusing the move");
        Assert.True(Move.RefusedBy(12));
    }

    /// <summary>
    /// Until the answer every report, a hand's included, is from before the move and measured from the
    /// old origin; from the answer on, from the new one.
    /// </summary>
    [Fact]
    public void AReportIsMeasuredFromTheOriginItWasMadeIn()
    {
        Assert.Equal(Move.From, Move.MeasuredFor(0));
        Assert.Equal(Move.From, Move.MeasuredFor(11));
        Assert.Equal(Move.To, Move.MeasuredFor(12));
        Assert.Equal(Move.To, Move.MeasuredFor(40));
    }
}
