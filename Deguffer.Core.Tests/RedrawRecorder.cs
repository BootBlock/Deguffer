using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>What a redraw asked of its target.</summary>
internal enum RedrawCall
{
    Begin,
    Land,
    Arrive,
    Withdraw,
}

/// <summary>A target that keeps every call made of it, with the regions each hand-over put up.</summary>
internal sealed class RedrawRecorder : ICanvasRedrawTarget
{
    public List<(CanvasRedraw Redraw, RedrawCall Call)> Calls { get; } = [];

    public List<(CanvasRedraw Redraw, CanvasRegion Region)> Landed { get; } = [];

    public void Begin(CanvasRedraw redraw) => Calls.Add((redraw, RedrawCall.Begin));

    public void Land(CanvasRedraw redraw, IReadOnlyList<CanvasRegion> regions)
    {
        Calls.Add((redraw, RedrawCall.Land));
        Landed.AddRange(regions.Select(region => (redraw, region)));
    }

    public void Arrive(CanvasRedraw redraw) => Calls.Add((redraw, RedrawCall.Arrive));

    public void Withdraw(CanvasRedraw redraw) => Calls.Add((redraw, RedrawCall.Withdraw));

    /// <summary>The calls made for <paramref name="redraw"/>, in order.</summary>
    public IReadOnlyList<RedrawCall> Of(CanvasRedraw redraw) =>
        [.. Calls.Where(call => call.Redraw == redraw).Select(call => call.Call)];
}
