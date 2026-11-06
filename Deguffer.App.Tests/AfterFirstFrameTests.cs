using Deguffer.App.Shell;

namespace Deguffer.App.Tests;

/// <summary>
/// A tool window left open at the last close is opened again after the main window's first frame,
/// not in the launch turn that frame is waiting for, and only once.
/// </summary>
public sealed class AfterFirstFrameTests
{
    private readonly SteppedFrameClock _frames = new();

    private int _runs;

    [Fact]
    public void NothingRunsInTheTurnThatAsksForIt()
    {
        AfterFirstFrame.Run(_frames, () => _runs++);

        Assert.Equal(0, _runs);
        Assert.True(_frames.IsTicking);
    }

    /// <summary>The first tick is raised before its frame is laid out, so that frame is not drawn yet.</summary>
    [Fact]
    public void NothingRunsAtTheFirstFrameTick()
    {
        AfterFirstFrame.Run(_frames, () => _runs++);

        _frames.Step(TimeSpan.FromMilliseconds(16));

        Assert.Equal(0, _runs);
    }

    [Fact]
    public void ItRunsOnceAtTheFrameAfterTheFirstAndStopsListening()
    {
        AfterFirstFrame.Run(_frames, () => _runs++);

        _frames.Step(TimeSpan.FromMilliseconds(16));
        _frames.Step(TimeSpan.FromMilliseconds(16));

        Assert.Equal(1, _runs);
        Assert.False(_frames.IsTicking);

        _frames.Step(TimeSpan.FromMilliseconds(16));
        _frames.Step(TimeSpan.FromMilliseconds(16));

        Assert.Equal(1, _runs);
    }

    /// <summary>A frame raised from inside the action, as a window shown there can raise, does not run it again.</summary>
    [Fact]
    public void AFrameRaisedWhileItRunsDoesNotRunItAgain()
    {
        // One frame from the first run only, so a second run is counted rather than recursing.
        AfterFirstFrame.Run(_frames, () =>
        {
            if (_runs++ == 0)
            {
                _frames.Step(TimeSpan.FromMilliseconds(16));
            }
        });

        _frames.Step(TimeSpan.FromMilliseconds(16));
        _frames.Step(TimeSpan.FromMilliseconds(16));

        Assert.Equal(1, _runs);
    }
}
