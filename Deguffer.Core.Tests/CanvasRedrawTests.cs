using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// A redraw is laid out and painted on workers and put on screen on its owner's thread, and only the
/// newest is wanted.
///
/// <para>The guarantee worth proving is the negative one. A superseded redraw puts nothing on
/// screen, whatever it had finished on the workers and posted to the owner before it was superseded:
/// a region of it landing after a newer picture is asked for would show part of a picture nobody
/// wants, over the one that replaces it, and a click there would be resolved against it (§7.1).</para>
/// </summary>
public sealed class CanvasRedrawTests
{
    private static readonly int RegionCount = PaintOrder.Regions(Drawings.Size, Drawings.Size, focus: null).Count;

    private readonly QueuedContext _owner = new();
    private readonly RedrawRecorder _target = new();
    private readonly CanvasRedraws _redraws;

    public CanvasRedrawTests() => _redraws = new CanvasRedraws(_owner);

    /// <summary>
    /// Every region lands once, then the redraw arrives, and the buffer holds the picture the drawing
    /// paints whole.
    /// </summary>
    [Fact]
    public void ARedrawLandsEveryRegionOnceAndThenArrives()
    {
        var drawing = Drawings.Covering(Drawings.Tree(), node: 1);
        var redraw = Start(() => drawing);

        _owner.RunUntil(() => redraw.IsArrived, "the redraw to arrive");

        var calls = _target.Of(redraw);

        Assert.Equal(RedrawCall.Begin, calls[0]);
        Assert.Equal(RedrawCall.Arrive, calls[^1]);
        Assert.All(calls.Skip(1).SkipLast(1), call => Assert.Equal(RedrawCall.Land, call));

        Assert.Equal(
            PaintOrder.Regions(Drawings.Size, Drawings.Size, focus: null).OrderBy(Key),
            _target.Landed.Select(landed => landed.Region).OrderBy(Key));

        var whole = new byte[redraw.Pixels.Length];
        drawing.Painter(Drawings.Ground).PaintRegions(whole);

        Assert.Equal(whole, redraw.Pixels);
        Assert.Same(drawing, redraw.Surface);
    }

    /// <summary>
    /// Superseded while it lays out, a redraw never begins: the target hears nothing of it at all,
    /// and the newer one lands in full.
    /// </summary>
    [Fact]
    public async Task ARedrawSupersededWhileItLaysOutPutsNothingOnScreen()
    {
        using var layingOut = new ManualResetEventSlim();
        using var laidOut = new ManualResetEventSlim();
        var older = Start(() =>
        {
            layingOut.Set();
            laidOut.Wait(TimeSpan.FromSeconds(20));
            return Drawings.Covering(Drawings.Tree(), node: 1);
        });

        // Inside its layout, so the layout finishes after the redraw is superseded rather than never
        // starting, and what it returns is what must not reach the screen.
        Assert.True(layingOut.Wait(TimeSpan.FromSeconds(20)), "the older redraw never started laying out");

        var newer = Start(() => Drawings.Covering(Drawings.Tree(), node: 2));

        laidOut.Set();
        _owner.RunUntil(() => newer.IsArrived, "the newer redraw to arrive");
        await older.Finished.WaitAsync(TimeSpan.FromSeconds(20));
        _owner.RunPending();

        Assert.Empty(_target.Of(older));
        Assert.True(older.IsSuperseded);
        Assert.Null(older.Surface);
        Assert.Equal(RegionCount, _target.Landed.Count(landed => landed.Redraw == newer));
    }

    /// <summary>
    /// A redraw that finished painting, and posted every region to its owner, before it was
    /// superseded still puts none of them on screen: the owner had not run them yet, and by the time
    /// it does they are not wanted.
    /// </summary>
    [Fact]
    public async Task ARedrawSupersededAfterItPaintedPutsNothingOnScreen()
    {
        var older = Start(() => Drawings.Covering(Drawings.Tree(), node: 1));

        await older.Finished.WaitAsync(TimeSpan.FromSeconds(20));

        var newer = Start(() => Drawings.Covering(Drawings.Tree(), node: 2));

        _owner.RunUntil(() => newer.IsArrived, "the newer redraw to arrive");
        _owner.RunPending();

        Assert.Empty(_target.Of(older));
        Assert.False(older.Covers(1, 1));
    }

    /// <summary>
    /// Superseded part of the way through landing, a redraw is withdrawn, so what of it was on
    /// screen comes off; and nothing more of it lands, though its workers go on to finish the region
    /// they were painting.
    /// </summary>
    [Fact]
    public async Task ARedrawSupersededWhileItLandsIsWithdrawnAndLandsNoMore()
    {
        var gated = new GatedSurface(Drawings.Covering(Drawings.Tree(), node: 1), region => region is { X: 0, Y: 0 });
        var older = Start(() => gated, new ExplorePoint(1, 1));

        _owner.RunUntil(() => older.Landed.Count > 0, "the first region to land");

        Assert.True(older.Covers(1, 1));

        var newer = Start(() => Drawings.Covering(Drawings.Tree(), node: 2));

        Assert.Equal([RedrawCall.Begin, RedrawCall.Land, RedrawCall.Withdraw], _target.Of(older));
        Assert.False(older.Covers(1, 1));

        gated.Open();
        _owner.RunUntil(() => newer.IsArrived, "the newer redraw to arrive");
        await older.Finished.WaitAsync(TimeSpan.FromSeconds(20));
        _owner.RunPending();

        Assert.Equal([RedrawCall.Begin, RedrawCall.Land, RedrawCall.Withdraw], _target.Of(older));
    }

    /// <summary>
    /// The next redraw does not touch the buffer until the one it superseded has stopped. The two
    /// share it, so a newer redraw painting while the older one still is would mix the two pictures
    /// in the regions they both reach.
    /// </summary>
    [Fact]
    public void ANewerRedrawWaitsForTheOlderToStop()
    {
        using var layingOut = new ManualResetEventSlim();
        using var laidOut = new ManualResetEventSlim();
        var olderStopped = (bool?)null;

        var older = Start(() =>
        {
            layingOut.Set();
            laidOut.Wait(TimeSpan.FromSeconds(20));
            return Drawings.Covering(Drawings.Tree(), node: 1);
        });

        // Inside its layout, so it is running when it is superseded rather than stopping before it
        // starts, which would leave nothing for the newer one to wait for.
        Assert.True(layingOut.Wait(TimeSpan.FromSeconds(20)), "the older redraw never started laying out");

        using var newerLayingOut = new ManualResetEventSlim();

        var newer = Start(() =>
        {
            olderStopped = older.Finished.IsCompleted;
            newerLayingOut.Set();
            return Drawings.Covering(Drawings.Tree(), node: 2);
        });

        // Long enough for a newer redraw that does not wait to have started, while the older one is
        // still held in its layout. One that waits does not start, and is let go of after this.
        newerLayingOut.Wait(TimeSpan.FromMilliseconds(500));

        laidOut.Set();
        _owner.RunUntil(() => newer.IsArrived, "the newer redraw to arrive");

        Assert.True(olderStopped, "the newer redraw laid out while the older one was still running");
    }

    /// <summary>Once arrived, a redraw is the whole picture, and stopping the canvas's redraws does not withdraw it.</summary>
    [Fact]
    public void AnArrivedRedrawIsNotWithdrawn()
    {
        var redraw = Start(() => Drawings.Covering(Drawings.Tree(), node: 1));

        _owner.RunUntil(() => redraw.IsArrived, "the redraw to arrive");

        Assert.Null(_redraws.Pending);

        _redraws.Cancel();

        Assert.False(redraw.IsSuperseded);
        Assert.DoesNotContain(RedrawCall.Withdraw, _target.Of(redraw));
        Assert.False(redraw.Covers(1, 1));
    }

    /// <summary>
    /// A layout that fails is a defect, and it is raised on the owner's thread, where the
    /// application's handling of an unexpected failure sees it, rather than lost with the worker's
    /// task.
    /// </summary>
    [Fact]
    public void ALayoutThatFailsIsRaisedOnTheOwnersThread()
    {
        var redraw = Start(() => throw new InvalidOperationException("the layout failed"));

        var raised = Assert.Throws<InvalidOperationException>(
            () => _owner.RunUntil(() => false, "the failure to be raised"));

        Assert.Equal("the layout failed", raised.Message);
        Assert.Empty(_target.Of(redraw));
    }

    private CanvasRedraw Start(Func<ExploreSurface> layout, ExplorePoint? focus = null) =>
        _redraws.Start(
            layout,
            new byte[PixelBuffer.LengthFor(Drawings.Size, Drawings.Size)],
            Drawings.Ground,
            focus,
            _target);

    private static int Key(CanvasRegion region) => (region.Y * Drawings.Size) + region.X;
}
