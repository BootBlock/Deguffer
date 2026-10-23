using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.Graphics.Canvas;
using WinRT;
using static Deguffer.App.Controls.Direct3D;

namespace Deguffer.App.Controls;

/// <summary>
/// Shades a canvas of <see cref="CushionQuads"/> on the GPU, into a bitmap Win2D can draw from.
///
/// <para>Direct3D 11 rather than a Win2D effect, because the cost of a GPU picture is in handing the
/// GPU its shapes, not in shading them. Win2D draws one rectangle per call, and a large tree is sixty
/// thousand of them at 4K: 20 to 40 ms of calls, which is slower than the CPU's whole picture. Here
/// every quad goes up in one copy and is drawn by one instanced call, which shades a 4K canvas in under
/// a millisecond on a discrete GPU and about six on an integrated one.
/// <c>docs/todo/composition-stack.md</c> has the measurements.</para>
///
/// <para>On the device under Win2D's own, so what is shaded here is drawn into the map's surfaces
/// with no copy between devices, and is replaced with them when the device is lost. Every call is
/// made inside the device's lock, which is the one Win2D draws inside.</para>
/// </summary>
internal sealed class CushionRenderer : IDisposable
{
    private const int QuadSize = 24;

    /// <summary>How many floats the shader's constant buffer holds: see <c>Cushions.hlsl</c>.</summary>
    private const int CanvasConstants = 24;

    private static readonly Lazy<(byte[] Vertices, byte[] Pixels)> Shaders = new(Compile);

    /// <summary>The names the shader's inputs go by. Made once, and never freed: every layout reads them.</summary>
    private static readonly InputElement[] Inputs =
    [
        Input("EDGES", FormatR32G32B32A32Sint, 0),
        Input("RIDGE", FormatR32Float, 16),
        Input("COLOUR", FormatR8G8B8A8Uint, 20),
    ];

    private readonly CanvasDevice _device;

    private readonly IDevice _direct3D;

    private readonly IDeviceContext _context;

    private readonly nint _vertexShader;

    private readonly nint _pixelShader;

    private readonly nint _layout;

    private readonly nint _constants;

    private nint _quads;

    /// <summary>The quads in the buffer, so a light moving over one canvas does not send them again.</summary>
    private CushionQuads? _uploaded;

    private int _capacity;

    private nint _texture;

    private nint _view;

    private CanvasBitmap? _bitmap;

    private CushionRenderer(CanvasDevice device, IDevice direct3D)
    {
        _device = device;
        _direct3D = direct3D;
        _direct3D.GetImmediateContext(out _context);

        OnSoftware = IsSoftware(direct3D);

        var (vertices, pixels) = Shaders.Value;

        Check(_direct3D.CreateVertexShader(vertices, (nuint)vertices.Length, 0, out _vertexShader));
        Check(_direct3D.CreatePixelShader(pixels, (nuint)pixels.Length, 0, out _pixelShader));
        Check(_direct3D.CreateInputLayout(Inputs, (uint)Inputs.Length, vertices, (nuint)vertices.Length, out _layout));
        Check(_direct3D.CreateBuffer(
            new BufferDescription { ByteWidth = CanvasConstants * sizeof(float), BindFlags = BindConstantBuffer },
            0,
            out _constants));
    }

    /// <summary>
    /// Whether the device runs on the CPU, as WARP does where there is no GPU to use. It shades the same
    /// picture, but with the CPU's time, on the thread that asks.
    /// </summary>
    public bool OnSoftware { get; }

    /// <summary>The bitmap the last canvas was shaded into, at its corner, or null before the first.</summary>
    public CanvasBitmap? Shaded => _bitmap;

    /// <summary>
    /// A renderer on <paramref name="device"/>, or null where the device cannot run its shaders: one
    /// below Direct3D feature level 10, which the CPU paints for instead.
    /// </summary>
    public static CushionRenderer? For(CanvasDevice device)
    {
        var direct3D = DeviceUnder(device);

        if (direct3D.GetFeatureLevel() < FeatureLevel10)
        {
            Marshal.ReleaseComObject(direct3D);
            return null;
        }

        return new CushionRenderer(device, direct3D);
    }

    /// <summary>
    /// Shade <paramref name="region"/> of <paramref name="quads"/>' canvas on <paramref name="ground"/>,
    /// under <paramref name="light"/> where there is one, and finished as the quads are, and return the
    /// bitmap it is shaded in. The canvas is its corner, <see cref="CushionQuads.Width"/>
    /// by <see cref="CushionQuads.Height"/> pixels, and the region holds the picture until the next
    /// shading. Nothing else of the bitmap is.
    /// </summary>
    public CanvasBitmap Shade(CushionQuads quads, TileColour ground, CanvasRegion region, PointerLight? light)
    {
        using var held = _device.Lock();

        var bitmap = Fit(quads.Width, quads.Height);
        var shapes = quads.Quads;

        _context.ClearRenderTargetView(
            _view,
            new Vector4(ground.Red / 255f, ground.Green / 255f, ground.Blue / 255f, 1));

        if (shapes.Length > 0)
        {
            if (!ReferenceEquals(quads, _uploaded))
            {
                Upload(shapes);
                _uploaded = quads;
            }

            Draw(quads.Width, quads.Height, region, light, quads.Finish, shapes.Length);
        }

        return bitmap;
    }

    public void Dispose()
    {
        _bitmap?.Dispose();

        foreach (var pointer in (ReadOnlySpan<nint>)[_view, _texture, _quads, _constants, _layout, _pixelShader, _vertexShader])
        {
            if (pointer != 0)
            {
                Marshal.Release(pointer);
            }
        }

        Marshal.ReleaseComObject(_context);
        Marshal.ReleaseComObject(_direct3D);
    }

    /// <summary>Whether <paramref name="direct3D"/> runs on a software adapter.</summary>
    private static bool IsSoftware(IDevice direct3D)
    {
        // A cast of a COM wrapper asks the object for the interface.
        var dxgi = (IDxgiDevice)direct3D;

        Check(dxgi.GetAdapter(out var adapter));

        try
        {
            Check(adapter.GetDesc1(out var description));

            return (description.Flags & SoftwareAdapter) != 0;
        }
        finally
        {
            Marshal.ReleaseComObject(adapter);
        }
    }

    /// <summary>The Direct3D device under <paramref name="device"/>.</summary>
    private static IDevice DeviceUnder(CanvasDevice device)
    {
        var unknown = MarshalInterface<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice>.FromManaged(device);

        try
        {
            var access = (IDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(unknown);

            Check(access.GetInterface(DeviceId, out var native));

            try
            {
                return (IDevice)Marshal.GetObjectForIUnknown(native);
            }
            finally
            {
                Marshal.Release(native);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// Compile the shaders once for the process: the code is the same on every device. A failure here
    /// is a defect in the shader, raised with what the compiler said.
    /// </summary>
    private static (byte[] Vertices, byte[] Pixels) Compile()
    {
        using var stream = typeof(CushionRenderer).Assembly.GetManifestResourceStream("Deguffer.App.Controls.Cushions.hlsl")
            ?? throw new InvalidOperationException("The cushion shader is not embedded in the application.");
        using var copy = new MemoryStream();

        stream.CopyTo(copy);

        var source = copy.ToArray();

        return (Compile(source, "vs", "vs_4_0"), Compile(source, "ps", "ps_4_0"));
    }

    private static byte[] Compile(byte[] source, string entry, string target)
    {
        var result = D3DCompile(source, (nuint)source.Length, "Cushions.hlsl", 0, 0, entry, target, CompileFlags, 0, out var code, out var errors);

        try
        {
            if (result < 0 || code is null)
            {
                throw new InvalidOperationException(
                    $"The cushion shader's {entry} did not compile (0x{result:X8}): {Text(errors)}");
            }

            var bytes = new byte[(int)code.GetBufferSize()];
            Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);

            return bytes;
        }
        finally
        {
            Release(code);
            Release(errors);
        }
    }

    private static string Text(IBlob? blob) =>
        blob is null ? "no message" : Marshal.PtrToStringAnsi(blob.GetBufferPointer(), (int)blob.GetBufferSize()).TrimEnd('\0', '\n', '\r');

    private static void Release(IBlob? blob)
    {
        if (blob is not null)
        {
            Marshal.ReleaseComObject(blob);
        }
    }

    private static InputElement Input(string name, uint format, uint offset) => new()
    {
        SemanticName = Marshal.StringToHGlobalAnsi(name),
        Format = format,
        AlignedByteOffset = offset,
        InputSlotClass = PerInstance,
        InstanceDataStepRate = 1,
    };

    /// <summary>
    /// A failure from the device. A device removed while it was asked is the one to expect, and the
    /// caller's handling of a lost device sees it by its result.
    /// </summary>
    private static void Check(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    /// <summary>
    /// The texture, at least <paramref name="width"/> by <paramref name="height"/>. One for every canvas,
    /// grown to the largest asked for, rather than one per size (G5): a map shows one size at a time,
    /// and a tile is a corner of it.
    /// </summary>
    private CanvasBitmap Fit(int width, int height)
    {
        if (_bitmap is { } bitmap && bitmap.SizeInPixels.Width >= width && bitmap.SizeInPixels.Height >= height)
        {
            return bitmap;
        }

        var size = _bitmap?.SizeInPixels;
        var description = new TextureDescription
        {
            Width = (uint)Math.Max(width, (int)(size?.Width ?? 0)),
            Height = (uint)Math.Max(height, (int)(size?.Height ?? 0)),
            MipLevels = 1,
            ArraySize = 1,
            Format = FormatB8G8R8A8Unorm,
            SampleCount = 1,
            BindFlags = BindRenderTarget | BindShaderResource,
        };

        _bitmap?.Dispose();
        _bitmap = null;
        Release(ref _view);
        Release(ref _texture);

        Check(_direct3D.CreateTexture2D(description, 0, out _texture));
        Check(_direct3D.CreateRenderTargetView(_texture, 0, out _view));

        var surface = Marshal.QueryInterface(_texture, DxgiSurfaceId, out var dxgi);
        Check(surface);

        try
        {
            Check(CreateDirect3D11SurfaceFromDXGISurface(dxgi, out var inspectable));

            try
            {
                return _bitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                    _device,
                    MarshalInterface<Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface>.FromAbi(inspectable));
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    /// <summary>Copy <paramref name="shapes"/> into the buffer the draw reads, grown to hold them.</summary>
    private void Upload(ReadOnlySpan<CushionQuad> shapes)
    {
        if (shapes.Length > _capacity)
        {
            Release(ref _quads);

            // Doubled, so a tree that grows during a scan grows it a few times rather than at every redraw.
            _capacity = Math.Max(shapes.Length, Math.Max(1024, _capacity * 2));

            Check(_direct3D.CreateBuffer(
                new BufferDescription { ByteWidth = (uint)(_capacity * QuadSize), BindFlags = BindVertexBuffer },
                0,
                out _quads));

            _uploaded = null;
        }

        var bytes = MemoryMarshal.AsBytes(shapes);

        _context.UpdateSubresource(
            _quads,
            0,
            new Box { Right = (uint)bytes.Length, Bottom = 1, Back = 1 },
            ref MemoryMarshal.GetReference(bytes),
            0,
            0);
    }

    /// <summary>
    /// Draw <paramref name="count"/> quads over <paramref name="region"/> of a canvas
    /// <paramref name="width"/> by <paramref name="height"/>, under <paramref name="light"/> where there
    /// is one. Every piece of state the draw depends on is set, because the device is shared with Win2D,
    /// and the target is let go of after, so Win2D can read it.
    /// </summary>
    private void Draw(int width, int height, CanvasRegion region, PointerLight? light, ShapeFinish finish, int count)
    {
        var shining = light ?? default;

        Span<float> constants =
        [
            width,
            height,
            shining.X,
            shining.Y,
            region.X,
            region.Y,
            region.Right,
            region.Bottom,
            shining.Reach,
            light is null ? 0 : shining.Strength,
            shining.Reach * PointerLight.Height,
            finish.Borders.Width,
            finish.Borders.Tone.Red,
            finish.Borders.Tone.Green,
            finish.Borders.Tone.Blue,
            (float)finish.Shading,
            finish.Borders.Blocks,
            finish.Borders.Folders,
            finish.Bevel.Width,
            finish.Bevel.Highlight,
            finish.Bevel.Shadow,
            0,
            0,
            0,
        ];

        // The whole buffer, with no box: Direct3D 11 refuses a box on a constant buffer, and drops the
        // update without an error, which left every quad at a size of zero.
        _context.UpdateSubresource(
            _constants,
            0,
            in Unsafe.NullRef<Box>(),
            ref MemoryMarshal.GetReference(MemoryMarshal.AsBytes(constants)),
            0,
            0);

        _context.OMSetRenderTargets(1, _view, 0);
        _context.OMSetBlendState(0, 0, uint.MaxValue);
        _context.OMSetDepthStencilState(0, 0);
        _context.RSSetState(0);
        _context.RSSetViewports(1, new Viewport { Width = width, Height = height, MaxDepth = 1 });
        _context.IASetInputLayout(_layout);
        _context.IASetVertexBuffers(0, 1, _quads, QuadSize, 0);
        _context.IASetPrimitiveTopology(TriangleStrip);
        _context.VSSetShader(_vertexShader, 0, 0);
        _context.VSSetConstantBuffers(0, 1, _constants);
        _context.GSSetShader(0, 0, 0);
        _context.PSSetShader(_pixelShader, 0, 0);
        _context.PSSetConstantBuffers(0, 1, _constants);

        _context.DrawInstanced(4, (uint)count, 0, 0);

        _context.OMSetRenderTargets(1, 0, 0);
    }

    private static void Release(ref nint pointer)
    {
        if (pointer != 0)
        {
            Marshal.Release(pointer);
            pointer = 0;
        }
    }
}
