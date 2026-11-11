using Deguffer.App.Diagnostics;

namespace Deguffer.App.Tests;

/// <summary>
/// Where the <c>Launch</c> pair begins: before the application runs, so the stores its statics read
/// are inside the span, and measured from the process's creation.
/// </summary>
[Collection(ShellEventsListeningCollection.Name)]
public sealed class LaunchTimingTests
{
    /// <summary>
    /// The start is written before the application runs, not by it, carrying how long the process
    /// had existed by then, to the nearest millisecond.
    /// </summary>
    [Fact]
    public void TheLaunchStartsBeforeTheApplicationRunsAndCountsFromTheProcessStart()
    {
        using var recorder = new ShellEventRecorder();
        IReadOnlyList<string>? raisedWhenTheApplicationRan = null;

        LaunchTiming.Run(
            ShellEvents.Log,
            () => TimeSpan.FromMilliseconds(412.6),
            () => raisedWhenTheApplicationRan = [.. recorder.Names]);

        Assert.Equal(["LaunchStart"], raisedWhenTheApplicationRan);
        Assert.Equal([413], recorder.Events.Single().Numbers);
    }

    /// <summary>
    /// With no trace recording, the process is not opened to read its creation time, and the
    /// application still runs.
    /// </summary>
    [Fact]
    public void WithNoTraceTheProcessStartIsNotRead()
    {
        var asked = false;
        var ran = false;

        LaunchTiming.Run(ShellEvents.Log, () =>
        {
            asked = true;
            return TimeSpan.Zero;
        }, () => ran = true);

        Assert.False(asked);
        Assert.True(ran);
    }
}
