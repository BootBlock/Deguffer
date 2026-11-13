using Deguffer.App.Shell;
using Deguffer.Core.Viewing;
using Windows.Foundation;

namespace Deguffer.App.Tests;

/// <summary>
/// The rows a change takes off the screen are ghosted together. Laying the ghost layer out lays out
/// the whole window, so the change's ghosts share one pass, however many rows leave.
/// </summary>
public sealed class RowGhostsTests
{
    private static readonly Motion Played = new(TimeSpan.FromMilliseconds(250), Travels: true);

    [Fact]
    public void TwentyRowsLeavingInOneChangeShareOneLayoutPass()
    {
        var layer = new RecordingLayer();
        var leaving = Leaving(20);

        RowGhosts.Leave(layer, leaving, Played);

        Assert.Equal(1, layer.Steps.Count(step => step == Step.LayOut));
        Assert.Equal(
            [.. Enumerable.Repeat(Step.Add, 20), Step.LayOut, .. Enumerable.Repeat(Step.Fade, 20)],
            layer.Steps);
    }

    /// <summary>
    /// Each ghost is faded and aligned from where its own row stood, not from another row's place,
    /// and with the motion the change played.
    /// </summary>
    [Fact]
    public void EachGhostIsFadedFromWhereItsOwnRowStood()
    {
        var layer = new RecordingLayer();
        var leaving = Leaving(5);

        RowGhosts.Leave(layer, leaving, Played);

        Assert.Equal(leaving.Select(row => row.Item), layer.Added);
        Assert.Equal(leaving.Select(row => (row.Item, row.Stood, Played)), layer.Faded);
    }

    [Fact]
    public void AChangeWithNoRowLeavingLaysNothingOut()
    {
        var layer = new RecordingLayer();

        RowGhosts.Leave(layer, [], Played);

        Assert.Empty(layer.Steps);
    }

    private static List<(object Item, RowGhosts.Stood Stood)> Leaving(int count) =>
        [.. Enumerable.Range(0, count).Select(row =>
            ((object)$"row {row}", new RowGhosts.Stood(new Rect(0, row * 32, 400, 32), null, null, null)))];

    private enum Step
    {
        Add,
        LayOut,
        Fade,
    }

    /// <summary>A layer that draws nothing and records each step asked of it, in order.</summary>
    private sealed class RecordingLayer : IGhostLayer<object>
    {
        public List<Step> Steps { get; } = [];

        public List<object> Added { get; } = [];

        public List<(object Ghost, RowGhosts.Stood Stood, Motion Played)> Faded { get; } = [];

        public object Add(object item, RowGhosts.Stood stood)
        {
            Steps.Add(Step.Add);
            Added.Add(item);

            return item;
        }

        public void LayOut() => Steps.Add(Step.LayOut);

        public void Fade(object ghost, RowGhosts.Stood stood, Motion played)
        {
            Steps.Add(Step.Fade);
            Faded.Add((ghost, stood, played));
        }
    }
}
