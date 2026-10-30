using System.Runtime.CompilerServices;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// The GPU device a compositor's surfaces are drawn through, and what a loss of it does: every map
/// draws through it (see <see cref="MapGraphics"/>), and the window takes its stills through it.
///
/// <para>One for each compositor, because the device is the costly thing and the window has the one
/// compositor (G5). Every surface is made on the one device, so a device lost is replaced for all of
/// them at once, and each map then makes its surfaces again: see <see cref="Replaced"/>.</para>
/// </summary>
internal sealed class CompositionDevice
{
    /// <summary>
    /// The format of every surface and bitmap drawn through the device: <see cref="PixelBuffer"/>'s byte
    /// order, blue, green, red and alpha, premultiplied. A surface in another order would show a painted
    /// canvas in the wrong colours, with nothing to say so.
    /// </summary>
    public const DirectXPixelFormat Format = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    /// <summary>
    /// <see cref="Format"/>, as Win2D's own bitmaps name it: they take the platform's format type, where
    /// the compositor's surfaces take the Windows App SDK's.
    /// </summary>
    public const Windows.Graphics.DirectX.DirectXPixelFormat BitmapFormat =
        Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized;

    public const DirectXAlphaMode Alpha = DirectXAlphaMode.Premultiplied;

    private static readonly ConditionalWeakTable<Compositor, CompositionDevice> Shared = [];

    private CanvasDevice _device;

    private CompositionDevice(Compositor compositor)
    {
        _device = new CanvasDevice();
        _device.DeviceLost += OnDeviceLost;

        Graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);
        Graphics.RenderingDeviceReplaced += (_, _) =>
        {
            Generation++;
            Replaced?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>
    /// The device was lost, and is about to be replaced: let go of anything made on it. Raised on the
    /// thread that met the loss, before <see cref="Current"/> is the new device.
    /// </summary>
    public event EventHandler? Lost;

    /// <summary>
    /// Every surface's content is gone: the device was lost and replaced, by this or by the compositor.
    /// Raised on the UI thread. Each map draws its picture again, and keeps none of what it had.
    ///
    /// <para>Nor does it keep its surfaces. One made before the replacement takes a write on the new
    /// device without an error and shows nothing of it, so the map stayed empty under its labels
    /// until each surface was made again.</para>
    /// </summary>
    public event EventHandler? Replaced;

    /// <summary>What makes the compositor's surfaces on the device.</summary>
    public CompositionGraphicsDevice Graphics { get; }

    /// <summary>The device drawn through now, which a loss replaces.</summary>
    public CanvasDevice Current => _device;

    /// <summary>
    /// How many times the device has been replaced, so a map that was off the screen when it happened
    /// knows on its return that what it kept is gone.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>The device for <paramref name="compositor"/>, made the first time it is asked for.</summary>
    public static CompositionDevice For(Compositor compositor) =>
        Shared.GetValue(compositor, static compositor => new CompositionDevice(compositor));

    /// <summary>
    /// Give back what the device keeps for itself between drawings, for a window nobody can see. What
    /// the surfaces hold is theirs and stays.
    /// </summary>
    public void Trim() => _device.Trim();

    /// <summary>
    /// What <paramref name="draw"/> returns on the device, or null where the device was lost on the way,
    /// which is then replaced.
    ///
    /// <para>The one failure to give up a drawing for: everything is drawn again on the new device. Any
    /// other failure is a defect, an invalid argument above all, and is left to be raised.</para>
    /// </summary>
    public T? UnlessLost<T>(Func<CanvasDevice, T?> draw)
        where T : class
    {
        var device = _device;

        try
        {
            return draw(device);
        }
        catch (Exception exception) when (IsLost(device, exception))
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="draw"/> on the device, unless the device is lost on the way. A pass can meet the
    /// same loss at several drawings, and skips each.
    /// </summary>
    public void UnlessLost(Action<CanvasDevice> draw) =>
        UnlessLost(device =>
        {
            draw(device);
            return this;
        });

    /// <summary>What <paramref name="draw"/> completes with, or null where the device was lost on the way.</summary>
    public async Task<T?> UnlessLostAsync<T>(Func<CompositionGraphicsDevice, Task<T>> draw)
        where T : class
    {
        var device = _device;

        try
        {
            return await draw(Graphics);
        }
        catch (Exception exception) when (IsLost(device, exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="exception"/>, met drawing on <paramref name="device"/>, says the device
    /// was lost, which is then reported. Several drawings can meet the same loss, and the device it was
    /// met on is reported lost only once.
    /// </summary>
    private bool IsLost(CanvasDevice device, Exception exception)
    {
        if (!device.IsDeviceLost(exception.HResult))
        {
            return false;
        }

        if (device == _device)
        {
            device.RaiseDeviceLost();
        }

        return true;
    }

    /// <summary>
    /// The device was lost: replace it under the compositor's graphics device, which raises
    /// <see cref="Replaced"/> once every surface is on the new one. Called by a test too, because
    /// Win2D refuses to report a device lost that is not.
    /// </summary>
    internal void OnDeviceLost(CanvasDevice sender, object? args)
    {
        sender.DeviceLost -= OnDeviceLost;

        Lost?.Invoke(this, EventArgs.Empty);

        _device = new CanvasDevice();
        _device.DeviceLost += OnDeviceLost;

        CanvasComposition.SetCanvasDevice(Graphics, _device);
    }
}
