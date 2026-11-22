using Deguffer.App.Controls;
using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Viewing;
using Deguffer.Testing;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Composition;
using Windows.Graphics;

namespace Deguffer.App.Tests;

/// <summary>
/// A whole drawing is copied small for the overview from what is already on the GPU. A drawing painted
/// on the CPU goes up once, a region at a time as it lands, and is never sent up whole a second time on
/// the UI thread to be shrunk.
///
/// <para>Each test lands real redraws, laid out and painted on workers, on a compositor of its own, so
/// the bytes counted are its drawings' alone. The compositor's thread is handed each step and never
/// waits on the workers there, so the tests that share that thread are not held up.</para>
/// </summary>
public sealed class ExploreMiniatureTests
{
    /// <summary>Two regions across and down, so a drawing lands in parts.</summary>
    private const int Side = PaintOrder.RegionSize * 2;

    private static readonly TileColour Ground = new(32, 32, 32);

    [Fact]
    public void AWholeDrawingPaintedOnTheCpuGoesUpOnce()
    {
        var (uploaded, overview) = OnAMap(map =>
        {
            map.Paint(Side, Side);

            return map.Result();
        });

        Assert.Equal(PixelBuffer.LengthFor(Side, Side), uploaded);
        Assert.True(overview, "no overview was made of the whole drawing");
    }

    /// <summary>
    /// A drawing shaded on the GPU is shrunk from the canvas it was shaded on, and nothing of it is
    /// painted on the CPU to go up at all.
    /// </summary>
    [Fact]
    public void AWholeDrawingShadedOnTheGpuIsCopiedWithoutAnUpload()
    {
        var (uploaded, overview) = OnAMap(map =>
        {
            map.Shade(Side, Side);

            return map.Result();
        });

        Assert.Equal(0, uploaded);
        Assert.True(overview, "no overview was made of the whole drawing");
    }

    /// <summary>
    /// A map resized larger paints its next drawing on a larger canvas, which goes up into a GPU canvas
    /// of the new size, once, rather than into the smaller one it outgrew.
    /// </summary>
    [Fact]
    public void ADrawingOfANewSizeGoesUpOnceIntoACanvasOfThatSize()
    {
        var (uploaded, overview) = OnAMap(map =>
        {
            map.Paint(Side / 2, Side / 2);
            map.Paint(Side, Side);

            return map.Result();
        });

        Assert.Equal(PixelBuffer.LengthFor(Side / 2, Side / 2) + PixelBuffer.LengthFor(Side, Side), uploaded);
        Assert.True(overview, "no overview was made of the whole drawing");
    }

    /// <summary>A map that lets go of its GPU canvas disposes of it, rather than leaving it to the finaliser.</summary>
    [Fact]
    public void ReleasingTheUploadDisposesItsCanvas()
    {
        var disposed = OnAMap(map =>
        {
            var canvas = map.Land(Side, Side)!;

            return map.On(() =>
            {
                map.Upload.Release();

                try
                {
                    _ = canvas.SizeInPixels;
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            });
        });

        Assert.True(disposed, "the released canvas was not disposed");
    }

    /// <summary>
    /// A drawing landing on a device that has replaced a lost one goes up into a canvas made on the new
    /// device, and the one made on the lost device is disposed: written into, it would take the write
    /// and show nothing.
    /// </summary>
    [Fact]
    public void ADeviceThatReplacesALostOneGetsACanvasOfItsOwn()
    {
        var (replaced, disposed) = OnAMap(map =>
        {
            var before = map.Land(Side, Side)!;

            // As Win2D reports a loss, which it does only for a device that is lost.
            map.On(() => map.Graphics.Device.OnDeviceLost(map.Graphics.Device.Current, null));

            var after = map.Land(Side, Side);

            return map.On(() =>
            {
                var onTheNewDevice = after is not null
                    && !ReferenceEquals(after, before)
                    && ReferenceEquals(after.Device, map.Graphics.Device.Current);

                try
                {
                    _ = before.SizeInPixels;
                    return (onTheNewDevice, false);
                }
                catch (ObjectDisposedException)
                {
                    return (onTheNewDevice, true);
                }
            });
        });

        Assert.True(replaced, "the drawing went up into the canvas made on the lost device");
        Assert.True(disposed, "the canvas made on the lost device was not disposed");
    }

    /// <summary>
    /// A canvas made on a device a loss has replaced, which a map keeps until it hears of the
    /// replacement, is neither written into nor shrunk: drawn through the new device, it would fail
    /// with an error that is not a loss, and reach the UI thread.
    /// </summary>
    [Fact]
    public void ACanvasOfALostDeviceIsNeitherWrittenNorShrunk()
    {
        var (uploaded, shrunk) = OnAMap(map =>
        {
            var stale = map.Land(Side, Side)!;

            // As Win2D reports a loss, which it does only for a device that is lost.
            map.On(() => map.Graphics.Device.OnDeviceLost(map.Graphics.Device.Current, null));

            return map.Through(stale);
        });

        Assert.Equal(0, uploaded);
        Assert.False(shrunk, "a copy was shrunk from the lost device's canvas");
    }

    /// <summary>Run <paramref name="work"/> on a map's drawings, on a compositor of the test's own.</summary>
    private static T OnAMap<T>(Func<Map, T> work)
    {
        var map = CompositionThread.Run(static () => new Map(new Compositor()));

        try
        {
            return work(map);
        }
        finally
        {
            map.On(map.Close);
        }
    }

    private static TiledSurface Picture(int width, int height) =>
        (TiledSurface)ExploreSurface.Create(
            Tree,
            Tree.RootNode,
            ExploreView.Treemap,
            width,
            height,
            1,
            1,
            ShapeColours.ByBranch(ExploreScheme.Standard),
            MapLook.Default,
            VolumeSpace.None);

    private static readonly ExploreTree Tree = Build();

    private static ExploreTree Build()
    {
        var builder = new ExploreTreeBuilder("Sample");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, 8).Select(i => new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: (8 - i) * 1000L))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>
    /// A map's drawings, on a screen large enough for an overview, with the redraws, paint buffer and
    /// upload a map lands them through. Made, used and closed on the compositor's thread, which each
    /// step is handed through <see cref="On{T}"/>.
    /// </summary>
    private sealed class Map
    {
        private readonly Compositor _compositor;

        private readonly QueuedContext _owner = new();

        private readonly CanvasRedraws _redraws;

        /// <summary>A surface for <see cref="Land"/> to write into, outside the map's drawings.</summary>
        private readonly CompositionVirtualDrawingSurface _surface;

        public Map(Compositor compositor)
        {
            _compositor = compositor;
            Graphics = MapGraphics.For(compositor);
            Upload = new PaintUpload(Graphics);
            Layers = new ExploreLayers(compositor, Graphics, new MapCamera(compositor), new ScriptedMotion(false), _owner);
            Layers.Fit(Side, Side, default);
            _redraws = new CanvasRedraws(_owner);
            _surface = Graphics.CreateSurface(Side, Side);
        }

        public MapGraphics Graphics { get; }

        public PaintUpload Upload { get; }

        public ExploreLayers Layers { get; }

        /// <summary>Paint a whole drawing <paramref name="width"/> by <paramref name="height"/> on the CPU, and land it on top.</summary>
        public void Paint(int width, int height) =>
            Arrive(On(() => _redraws.Start(() => Picture(width, height), new byte[PixelBuffer.LengthFor(width, height)], Ground, focus: null, Target())));

        /// <summary>Shade a whole drawing <paramref name="width"/> by <paramref name="height"/> on the GPU, and land it on top.</summary>
        public void Shade(int width, int height) =>
            Arrive(On(() => _redraws.Shade(() => Picture(width, height), Ground, Target())));

        /// <summary>Write every region of a blank canvas <paramref name="width"/> by <paramref name="height"/> up through the upload.</summary>
        public CanvasBitmap? Land(int width, int height) =>
            On(() => Upload.Land(_surface, new byte[PixelBuffer.LengthFor(width, height)], width, height, PaintOrder.Regions(width, height, focus: null)));

        /// <summary>
        /// Write one region of a blank canvas through <paramref name="canvas"/> and shrink it, and say
        /// how many bytes went up and whether a copy was made.
        /// </summary>
        public (long Uploaded, bool Shrunk) Through(CanvasBitmap canvas) =>
            On(() =>
            {
                var before = Graphics.Uploaded;

                Graphics.Write(
                    _surface,
                    canvas,
                    new byte[PixelBuffer.LengthFor(Side, Side)],
                    Side,
                    new CanvasRegion(0, 0, PaintOrder.RegionSize, PaintOrder.RegionSize));

                var shrunk = Graphics.Shrink(null, canvas, Side, Side, new SizeInt32(Side / 4, Side / 4));

                return (Graphics.Uploaded - before, shrunk is not null);
            });

        /// <summary>How many bytes have gone up, and whether the overview has a copy.</summary>
        public (long Uploaded, bool Overview) Result() => On(() => (Graphics.Uploaded, Layers.Overview is not null));

        /// <summary>Run <paramref name="work"/> on the compositor's thread, and return what it returns.</summary>
        public T On<T>(Func<T> work) => CompositionThread.Run(work);

        /// <summary>Run <paramref name="work"/> on the compositor's thread.</summary>
        public void On(Action work) =>
            On(() =>
            {
                work();
                return true;
            });

        public void Close()
        {
            Upload.Release();
            _compositor.Dispose();
        }

        private ICanvasRedrawTarget Target() => ExploreLanding.Replacement(Layers, static _ => { }, Motion.Instant, Upload);

        /// <summary>
        /// Run what the workers hand the owner's thread on the compositor's, until
        /// <paramref name="redraw"/> has arrived, waiting for each hand-over on the test's own thread.
        /// </summary>
        private void Arrive(CanvasRedraw redraw)
        {
            while (!On(() =>
            {
                _owner.RunPending();
                return redraw.IsArrived;
            }))
            {
                _owner.WaitUntilPosted("the whole drawing to arrive");
            }
        }
    }
}
