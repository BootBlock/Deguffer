using Deguffer.App.Controls;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition;

namespace Deguffer.App.Tests;

/// <summary>
/// The cushion shaders are compiled once, on a worker, from the moment a map's graphics are made, so
/// the first picture does not wait on the UI thread for a compilation it could have been spared.
///
/// <para>Each test compiles through shaders of its own, whose compilation it counts and can hold back.
/// The process's own are compiled by whichever test first draws through them, so whether they are
/// compiled says nothing about when.</para>
/// </summary>
public sealed class CushionShadersTests
{
    /// <summary>How long a test waits for a compilation that should happen, which a failure waits out.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The compilation is held until the test has seen <see cref="CushionShaders.Warm"/> return, which a
    /// compilation on the caller's thread would never let it do: it fails at the end of its patience.
    /// </summary>
    [Fact]
    public async Task WarmingCompilesOnAWorkerAndOnceForEveryRendererMadeAfter()
    {
        using var released = new ManualResetEventSlim();
        var compilations = 0;

        var shaders = new CushionShaders(() =>
        {
            if (!released.Wait(Patience))
            {
                throw new TimeoutException("The compilation was held on the thread that started it.");
            }

            Interlocked.Increment(ref compilations);

            return CushionShaders.Compile();
        });

        var warming = shaders.Warm();

        Assert.False(warming.IsCompleted);

        released.Set();
        await warming.WaitAsync(Patience);

        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var first = CushionRenderer.For(device, shaders) ?? throw new InvalidOperationException("WARP cannot run the cushion shaders.");
        using var second = CushionRenderer.For(device, shaders) ?? throw new InvalidOperationException("WARP cannot run the cushion shaders.");

        Assert.Equal(1, compilations);
    }

    /// <summary>A renderer that asks while the shaders are still compiling waits for that compilation, and starts none of its own.</summary>
    [Fact]
    public async Task AskingDuringTheWarmUpWaitsForTheOneCompilation()
    {
        using var released = new ManualResetEventSlim();
        var compilations = 0;

        var shaders = new CushionShaders(() =>
        {
            Interlocked.Increment(ref compilations);
            released.Wait(Patience);

            return CushionShaders.Compile();
        });

        var warming = shaders.Warm();
        var asking = Task.Run(() => shaders.Value);

        await Task.WhenAny(asking, Task.Delay(TimeSpan.FromMilliseconds(100)));

        Assert.False(asking.IsCompleted);

        released.Set();
        var (vertices, pixels) = await asking.WaitAsync(Patience);

        Assert.Same(vertices, (await warming).Vertices);
        Assert.Same(pixels, (await warming).Pixels);
        Assert.Equal(1, compilations);
    }

    /// <summary>A compilation that fails on the worker is raised to the renderer that asks, as the compiler raised it and not wrapped.</summary>
    [Fact]
    public async Task AFailedCompilationIsRaisedWithTheCompilersMessage()
    {
        const string Message = "The cushion shader's ps did not compile (0x80004005): Cushions.hlsl(1,1): error X3000";

        var shaders = new CushionShaders(() => throw new InvalidOperationException(Message));

        await Assert.ThrowsAsync<InvalidOperationException>(() => shaders.Warm());

        var failure = Assert.Throws<InvalidOperationException>(() => shaders.Value);

        Assert.Equal(Message, failure.Message);
    }

    /// <summary>
    /// Making a map's graphics starts the compilation, before anything asks whether a picture can be
    /// shaded. A map makes its graphics as it is constructed, so this is how the compilation runs while
    /// its page is built and its tree scanned or read.
    /// </summary>
    [Fact]
    public void MakingAMapsGraphicsStartsTheCompilation()
    {
        using var started = new ManualResetEventSlim();

        var shaders = new CushionShaders(() =>
        {
            started.Set();

            return CushionShaders.Compile();
        });

        CompositionThread.Run(() =>
        {
            using var compositor = new Compositor();

            return new MapGraphics(CompositionDevice.For(compositor), shaders);
        });

        Assert.True(started.Wait(Patience));
    }
}
