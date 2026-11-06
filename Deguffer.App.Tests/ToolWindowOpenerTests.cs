using Deguffer.App.Views;
using Deguffer.Core.Configuration;
using Deguffer.Testing;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Deguffer.App.Tests;

/// <summary>
/// A tool window the last session closed with open is built again only after the main window's first
/// frame, never in the launch turn that frame is waiting for, and once; one closed stays closed.
/// </summary>
public sealed class ToolWindowOpenerTests : IDisposable
{
    private static readonly TimeSpan Refresh = TimeSpan.FromMilliseconds(16);

    private readonly TempDirectory _temp = new();
    private readonly SteppedFrameClock _frames = new();
    private readonly WindowPlacementStore<ToolWindowMetrics> _store;
    private readonly List<RecordedToolWindow> _built = [];
    private readonly ToolWindowOpener _opener;

    public ToolWindowOpenerTests()
    {
        _store = new WindowPlacementStore<ToolWindowMetrics>(new FakeUserEnvironment(_temp.Path), "tool-window.json");
        _opener = new ToolWindowOpener(Build, _store, _frames);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AWindowLeftOpenIsNotBuiltInTheLaunchTurnNorAtTheFirstFrameTick()
    {
        LeaveTheWindow(open: true);

        _opener.Reopen();

        Assert.Empty(_built);

        _frames.Step(Refresh);

        Assert.Empty(_built);
    }

    [Fact]
    public void AWindowLeftOpenIsShownWithoutTheFocusOnceTheFirstFrameIsDrawn()
    {
        LeaveTheWindow(open: true);

        _opener.Reopen();
        _frames.Step(Refresh);
        _frames.Step(Refresh);

        var window = Assert.Single(_built);
        Assert.Equal(1, window.ShownWithoutFocus);
        Assert.Equal(0, window.Activated);
        Assert.False(_frames.IsTicking);

        _frames.Step(Refresh);
        _frames.Step(Refresh);

        Assert.Single(_built);
        Assert.Equal(1, window.ShownWithoutFocus);
    }

    [Fact]
    public void AWindowLeftClosedIsNotOpenedAndTheFramesAreLetGo()
    {
        LeaveTheWindow(open: false);

        _opener.Reopen();
        _frames.Step(Refresh);
        _frames.Step(Refresh);

        Assert.Empty(_built);
        Assert.False(_frames.IsTicking);
    }

    /// <summary>The reader opened it in the moment before the reopen ran: that window is the one shown, not a second.</summary>
    [Fact]
    public void AWindowOpenedBeforeTheReopenRunsIsTheOneItShows()
    {
        LeaveTheWindow(open: true);

        _opener.Reopen();
        _opener.Show();
        _frames.Step(Refresh);
        _frames.Step(Refresh);

        var window = Assert.Single(_built);
        Assert.Equal(1, window.Activated);
    }

    /// <summary>
    /// For a trace, the reopen is one span around building and showing the window, after the first
    /// frame and so outside the launch, and a window left closed raises none.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AReopenedWindowIsOneSpanAndOneLeftClosedIsNone(bool open)
    {
        LeaveTheWindow(open);
        using var recorder = new ShellEventRecorder();

        _opener.Reopen();
        _frames.Step(Refresh);

        Assert.Empty(recorder.Events);

        _frames.Step(Refresh);

        string[] spans = open ? ["ToolWindowsStart", "ToolWindowsStop"] : [];

        Assert.Equal(spans, recorder.Names);
        Assert.Equal(open ? 1 : 0, _built.Count);
    }

    private IToolWindow Build()
    {
        var window = new RecordedToolWindow();
        _built.Add(window);
        return window;
    }

    private void LeaveTheWindow(bool open) =>
        Assert.True(_store.Save(new ToolWindowMetrics(new WindowBounds(100, 100, 560, 860), open)));

    private sealed class RecordedToolWindow : IToolWindow
    {
        // Never raised: no test here closes the window.
        public event TypedEventHandler<object, WindowEventArgs> Closed
        {
            add { }
            remove { }
        }

        public int Activated { get; private set; }

        public int ShownWithoutFocus { get; private set; }

        public void Activate() => Activated++;

        public void ShowWithoutFocus() => ShownWithoutFocus++;

        public void Remember(bool isOpen)
        {
        }
    }
}
