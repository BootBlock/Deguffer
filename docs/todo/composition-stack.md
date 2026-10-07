# The composition stack for the map — spike

> **Status:** 🟢 ACTIVE — the spike is finished and its answers below are what the
> `direct-composition` work (#294) builds on. `Deguffer.App` now references
> `Microsoft.Graphics.Win2D`, and nothing draws through it yet. Four things stay open, listed under
> [Open before #274 lands](#open-before-274-lands): the Win2D package's licence terms, a monitor of
> a different scale, Remote Desktop, and an ARM64 machine. Re-measure against a newer Windows App
> SDK or Win2D before trusting the figures.

Before the Explore map moves off `WriteableBitmap`s onto the composition layer, this checks that the
stack it needs works in the build Deguffer ships: unpackaged, self-contained, untrimmed, and for
x86, x64 and ARM64. The stack is `Microsoft.UI.Composition`, which WinUI 3 is already drawn by, with
**Win2D** (`Microsoft.Graphics.Win2D`) drawing into composition surfaces. Raw DirectComposition is
not an option on its own: nothing supported puts an `IDCompositionVisual` into the WinUI 3 tree.

## How it was measured

A throwaway WinUI 3 app with `Deguffer.App`'s build settings (`WindowsPackageType=None`,
`SelfContained`, `WindowsAppSDKSelfContained`, ReadyToRun, untrimmed, the same manifest and the
`PublishGeneratedXamlArtefacts` target), published per runtime identifier. It ran a scripted probe
and wrote what it saw, then was screenshotted. Separately, `Deguffer.App` itself was published with
a `Microsoft.Graphics.Win2D` reference added, launched, and closed. The probe is not kept. The
package reference is.

| | |
| --- | --- |
| Windows App SDK | `Microsoft.WindowsAppSDK` 1.8.260921001 (WinUI 1.8.260803003, runtime 8000.994.2142.0) |
| Win2D | 1.4.0 |
| .NET SDK | 10.0.401 |
| Machine | Windows 11 build 26100, x64, a discrete GPU, two monitors both at 100% |

## The answers

| Question | Answer |
| --- | --- |
| Package pairing | Win2D 1.4.0 depends on `Microsoft.WindowsAppSDK.WinUI` ≥ 1.8.260204000. The App's float `1.*` resolves WinUI 1.8.260803003, which satisfies it. |
| Surface type | `CompositionVirtualDrawingSurface`, from `CanvasComposition.CreateCompositionGraphicsDevice`, on a `SpriteVisual`. [Why](#the-chosen-surface). |
| `SwapChainPanel` | Not used. Composition surfaces show the backdrop through transparent pixels, observed over `DesktopAcrylicBackdrop`. |
| Device loss | Replacing the device raises `RenderingDeviceReplaced` once, on the UI thread, and every surface redraws on the new device, WARP included. Oversized surfaces can break this: see [the traps](#two-traps-found-on-the-way). [Detail](#device-loss). |
| Remote Desktop and WARP | `new CompositionCapabilities()` reports effects supported and fast on this machine. Remote Desktop is **not measured**. [What the map turns off](#when-effects-are-slow). |
| DPI | The sprite is scaled by `1 / RasterizationScale`. A scale change is **not observed**: both monitors are at 100%. [Detail](#dpi). |
| Trimming | Not exercised: Deguffer does not trim. The Win2D projection assembly carries `IsTrimmable=True`. |
| No runtime installed | Every Windows App SDK and Win2D module loads from the app's own folder, on x64 and x86, for the probe and for `Deguffer.App`. [Detail](#self-contained-and-the-three-architectures). |
| ARM64 | Publishes, and every native binary is ARM64. **Not run**: no ARM64 machine. |
| Cost | Two files and 4.25 MB on x64: 525 files / 266.09 MB becomes 527 files / 270.34 MB. |

## Minimum package versions

- `Microsoft.Graphics.Win2D` **1.4.0**. It is the first release that depends on the WinUI component
  package alone. Releases 1.3.x depend on the whole `Microsoft.WindowsAppSDK` 1.6 metapackage.
- `Microsoft.WindowsAppSDK.WinUI` **1.8.260204000**, which Win2D 1.4.0 states. The App states the
  metapackage, and `1.*` resolves above it.
- `1.*` does not move to Windows App SDK 2.x, which NuGet already lists (2.5.1). Win2D's dependency
  range is open-ended, so a move to 2.x is a separate decision that re-runs this spike.

## The chosen surface

A `CompositionVirtualDrawingSurface`, created from the `CompositionGraphicsDevice` that
`CanvasComposition.CreateCompositionGraphicsDevice` returns, painted by a `CompositionSurfaceBrush`
(`Stretch = None`, alignment 0) onto a `SpriteVisual` that `ElementCompositionPreview.SetElementChildVisual`
hosts under the map. Tiles are written with `CanvasComposition.CreateDrawingSession(surface, rect, 96)`.
The update rectangle is always in pixels, and the session's 96 DPI makes the drawing units pixels too.

- **Size.** A virtual surface of 16,777,216 × 16,777,216 px was created and a tile drawn in its far
  corner. 1,073,741,823 px square is refused with `E_INVALIDARG`. A plain `CompositionDrawingSurface`
  from `CreateDrawingSurface(Size)` was drawn in full at 16,384 px, the device's largest texture
  here. Its upper limit was not measured. `CreateDrawingSurface2` refuses 32,768 px.
- **Sparse.** `Trim` over the whole surface, followed by a redraw, worked. That only written tiles
  hold memory, and that `Trim` releases them, is documented behaviour. Memory was not measured.
- **The camera is a visual property.** Offset and scale on a visual animate on the compositor
  thread, which #274 and #276 depend on. That is documented behaviour: the probe did not move the
  sprite.
- **Recording cost.** Recording a 256 × 256 px tile costs 0.01–0.10 ms of UI-thread time, on the GPU
  and on WARP, on x64 and x86. This is CPU time to record the commands only: the GPU runs them later, and its
  time was not measured.

**`CanvasVirtualControl` is not chosen.** It works: it stays transparent, it reports
`CreateResources` with `NewDevice` on every device change and then invalidates its whole visible
region. It is documented to track DPI on its own, which was not observed. But it is a XAML element. Panning it means moving it with
XAML layout or a render transform from the UI thread, and its `RegionsInvalidated` follows its own
visible region rather than the map's camera. That is the per-frame UI-thread work #274 removes.

### Two traps found on the way

**Win2D cannot draw into a `CreateDrawingSurface2` surface.** A surface made with
`CompositionGraphicsDevice.CreateDrawingSurface2` (the `SizeInt32` overload) is created, but every
Win2D `CreateDrawingSession` on it fails with `E_INVALIDARG`, at every size tried from 4,096 to
16,384 px. Create surfaces with `CreateVirtualDrawingSurface`, or `CreateDrawingSurface` with a
`Size`.

**After oversized surfaces, a device replacement can leave the map white.** In a run that first
created the surfaces above (virtual surfaces up to 16,777,216 px with a tile drawn, plain surfaces
drawn in full at 16,384 px, `CreateDrawingSurface2` surfaces up to 16,384 px and one refused at
32,768 px), the next `SetCanvasDevice` left the map's surface white on screen. Every redraw after it
reported success. The cause is not identified. What the runs show:

| Created before the replacement | White on screen |
| --- | --- |
| All of them | 12 of 12 runs |
| All except the `CreateDrawingSurface2` surfaces | 0 of 6 |
| The `CreateDrawingSurface2` surfaces with the virtual ones, or with the plain ones | 6 of 6 |
| All, with `CreateDrawingSurface2` only at the refused 32,768 px | 3 of 3 |
| All, with `CreateDrawingSurface2` only at 16,384 px | 1 of 3 |
| All, with `CreateDrawingSurface2` only at 4,096 px | 0 of 3 |
| The `CreateDrawingSurface2` surfaces alone | 2 of 9 |
| The refused 32,768 px `CreateDrawingSurface2` call alone | 0 of 4 |
| Any one of the other probes alone | 0 of 10 |
| None of them | 0 of 3 |

Whether the `CreateDrawingSurface2` surfaces were drawn into made no difference. The refused
32,768 px call, beside the other large surfaces, caused it every time. Two rules follow for the map:

- Size every surface to what it shows. The map uses one virtual surface written in small tiles, and
  it never probes the device's limits at run time.
- Test a device recovery by what the screen shows, not by the redraw's success. Every failed run here
  reported success.

## Device loss

A real device loss cannot be forced on a shared machine without resetting the GPU for every
application on it, so the recovery path was driven directly, as the issue asks.

- `CanvasDevice.RaiseDeviceLost()` **refuses on a healthy device** (`E_INVALIDARG`, "This API was
  unexpectedly called when the Direct3D device is not lost"). It reports a loss that a draw has already met. It cannot
  simulate one.
- `CanvasComposition.SetCanvasDevice(graphicsDevice, newDevice)` raised `RenderingDeviceReplaced`
  exactly once, on the UI thread. Surfaces created before the swap accepted a redraw, and a surface
  created after it worked. This held for a new hardware device, for the same device, for WARP
  (`new CanvasDevice(forceSoftwareRenderer: true)`), and for the swap back to hardware.
- WARP's largest bitmap is 8,388,608 px against 16,384 on the GPU, so nothing sized to the GPU's
  limit breaks on WARP.

The map handles loss in one place:

1. Wrap each tile write. When it throws, ask `device.IsDeviceLost(hresult)`. If it is lost, call
   `device.RaiseDeviceLost()`.
2. On `CanvasDevice.DeviceLost`, create a new `CanvasDevice`, move the `DeviceLost` handler from
   the old device to it, and pass it to `SetCanvasDevice`. Several tile writes can fail in one pass,
   so a loss already being handled is not raised again.
3. On `RenderingDeviceReplaced`, whatever its cause, redraw every visible tile and drop every cached
   tile. The compositor also raises it when its own device is lost. That is documented behaviour,
   and was not observed here.

## When effects are slow

The Windows App SDK has no `CompositionCapabilities.GetForCurrentView()`: construct
`new CompositionCapabilities()`. On this machine it reports `AreEffectsSupported` and
`AreEffectsFast` both true, outside a remote session. Its `Changed` event fired once in each run,
shortly after the handler was attached and with the value unchanged. The cause was not identified.
The value can change while the app runs, so the map reads it again on `Changed` rather than once at
startup.

- **`AreEffectsSupported` false:** no `CompositionEffectBrush` anywhere.
- **`AreEffectsFast` false:** no blur or anything that samples the backdrop, no pointer lighting
  (#284), and no shadow or glow for hover and selection (#281). Hover and selection draw as outlines
  into the surface, and cushions come from `CushionShading` on the CPU, as today.
- **Either way:** transforms and opacity animations stay, because they cost the compositor almost
  nothing. They follow the motion policy in #273.

## DPI

The surface is addressed in physical pixels and the visual in DIPs. The sprite is therefore sized
to the element's size times `XamlRoot.RasterizationScale` and scaled by `1 / RasterizationScale`, so
one surface pixel lands on one screen pixel. On `XamlRoot.Changed` with a new scale, the map refits
the sprite and redraws every visible tile. `ExploreMap` already follows `XamlRoot.Changed` for the
bitmap it draws today. Moving the window between two monitors at the same scale raised no
`XamlRoot.Changed`. A scale change was not observed.

## Self-contained and the three architectures

| | x86 | x64 | ARM64 |
| --- | --- | --- | --- |
| Publishes | yes | yes | yes |
| Native binaries' machine type | `014C` | `8664` | `AA64` |
| Full probe ran | yes, under WOW64 | yes | not run |
| `Deguffer.App` with Win2D | built, not run | published and started | built, not run |

The machine type was read from the PE header of `Proto.exe`, `Microsoft.Graphics.Canvas.dll`,
`Microsoft.UI.Xaml.dll`, `Microsoft.WindowsAppRuntime.dll` and `dcompi.dll`. `Deguffer.App` built for
x86 and ARM64 with no warnings, and each build's `Microsoft.Graphics.Canvas.dll` matched its
architecture.

The measuring machine has Windows App SDK runtimes 1.2 to 2.x installed, and Windows Sandbox is not
available on it, so an absent runtime could not be staged. The evidence is therefore what loads,
not a clean machine. In the probe and in `Deguffer.App` published with Win2D, `Microsoft.UI.Xaml.dll`,
`Microsoft.WindowsAppRuntime.dll`, `dcompi.dll` and `Microsoft.Graphics.Canvas.dll` loaded from the
app's own folder, and no module loaded from `WindowsApps`. `Deguffer.App` opened its window and exited
0 when closed.

## Open before #274 lands

- **The Win2D binary's licence.** The source repository (`microsoft/Win2D`) is MIT. The NuGet
  package is built by Microsoft and names its licence only by a URL,
  `https://www.microsoft.com/web/webpi/eula/eula_win2d_10012014.htm`, which now redirects to an
  unrelated page, so its terms cannot be read. The Windows App SDK packages Deguffer already
  redistributes carry their own `license.txt`, which permits redistribution of the files they place
  beside the app. Win2D's package carries none. The maintainer is checking which terms apply. If
  they do not allow redistribution, the alternatives are building Win2D from its MIT source, or calling Direct2D through
  `ICompositionDrawingSurfaceInterop` without Win2D. Everything above about composition surfaces
  holds for either, since Win2D's `CanvasComposition` is a wrapper over that interface.
- **A monitor of a different scale.** Move the map between a 100% and a 150% monitor, and check the
  tiles stay one pixel to one pixel and redraw once.
- **Remote Desktop.** Record what `AreEffectsFast` reports in a remote session, and that the map
  falls back as described above.
- **ARM64.** Start the published ARM64 build on ARM64 hardware.
