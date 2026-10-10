# The composition stack for the map — spike

> **Status:** 🟢 ACTIVE — the spike is finished and its answers below are what the
> `direct-composition` work (#294) builds on. Since #274 the Explore map draws through Win2D into
> composition surfaces: see [what landing it found](#what-landing-274-found). The maintainer has
> cleared the Win2D package's licence. Three things stay [open](#still-open): a monitor of a
> different scale, Remote Desktop, and an ARM64 machine. Re-measure against a newer Windows App SDK
> or Win2D before trusting the figures.

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
| Device loss | Replacing the device raises `RenderingDeviceReplaced` once, on the UI thread, and every surface redraws on the new device, WARP included. Surfaces that break the documented rules can break this: see [the white map](#the-white-map-after-a-device-replacement). [Detail](#device-loss). |
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
  corner. 1,073,741,823 px square is refused with `E_INVALIDARG`, matching the documented 2^24 px
  limit. A plain `CompositionDrawingSurface` is backed by one texture, and the compositor asks for
  one a pixel or two larger than the surface: the Direct3D debug layer shows 16,384 × 16,386 px
  refused for a 16,384 px surface, against the 16,384 px texture limit. A plain surface is not
  usable at the texture limit, even when Win2D reports the draw as successful.
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

### Rules a composition surface must follow

These are documented. The probe broke the first and the third on purpose to find the limits. The
other two are documented only.

- **The first draw into a plain surface must cover all of it.** A smaller first update rectangle
  fails with `E_INVALIDARG` ([`IDCompositionSurface::BeginDraw`](https://learn.microsoft.com/windows/win32/api/dcomp/nf-dcomp-idcompositionsurface-begindraw),
  and the sample in [Composition native interop](https://learn.microsoft.com/windows/apps/develop/composition/composition-native-interop)).
  A virtual surface has no such rule. Surfaces from `CreateDrawingSurface2` drew at 4,096, 8,192
  and 16,000 px once the first draw covered them.
- **One draw at a time per graphics device.** A second `BeginDraw` before `EndDraw` fails.
- **No plain surface at or past the device's texture limit,** and no virtual surface past 2^24 px.
- **An invalid-argument failure from `BeginDraw` is an application defect.** Microsoft's guide says
  to fail fast on it. Only a lost device is a failure to skip a frame for.

### The white map after a device replacement

After the limit probes above, the next device replacement left the map's surface opaque white on
screen, gaps included, while every redraw reported success. With every probe, 12 of 12 runs did
this. Without the `CreateDrawingSurface2` probes (partial first draws from 4,096 to 16,384 px, and a
refused 32,768 px creation), 0 of 6 did, although those runs still drew a plain 16,384 px surface
past the texture limit. With only some of the `CreateDrawingSurface2` probes, the result varied
from run to run.

What was ruled out, by measurement:

| Tried | White on screen |
| --- | --- |
| 0 to 6 plain 16,384 px surfaces, each with a partial first draw and a refused texture, and nothing else, then the replacement | 0 of 12 runs |
| After the probes: a new surface on the same graphics device | 3 of 3 |
| After the probes: a new graphics device on the new Direct3D device | 3 of 3 |
| After the probes: a new graphics device, with no `SetCanvasDevice` at all | 3 of 3 |
| After the probes: a new graphics device on the original Direct3D device | 3 of 3 |
| After the probes: a second Direct3D device created and left unused | 0 of 3 |
| After the probes: a new surface with no device change | 0 of 3 |
| The probe's GPU memory | 179 to 248 MB in every case, so not a leak |

So the fault is not GPU memory, and breaking the plain-surface rules alone does not cause it. Once
it happens, no in-process recovery that was tried clears it. Win2D's source holds no cache of
graphics devices or surfaces, and passes each call straight to the compositor's interop interfaces.
That suggests the damaged state is in the Windows App SDK compositor or below it, which was not
measured. No document describes a white fallback.
Microsoft states that an undrawn surface is transparent. No issue in `microsoft/Win2D`,
`microsoft/microsoft-ui-xaml` or `microsoft/WindowsAppSDK` reports it. Which internal state is
damaged is not known.

**The map's own pattern did not trigger it in 20 runs.** A workload shaped like the map's camera ran 400 steps
over one virtual surface. It panned the sprite, zoomed a container between 0.5× and 2× with 16
compositor scale animations, wrote 9,244 tiles of 256 px, and trimmed to a window of three
viewports 40 times. It then replaced the device and redrew. Each run was checked with a capture of
the probe window's own content. That capture was first checked against one run known to be white
and one known to be correct.

| Device replacement | Correct on screen |
| --- | --- |
| After the workload, to a new GPU device | 5 of 5 |
| After the workload, to WARP | 5 of 5 |
| During a 1.5 s zoom animation, to a new GPU device | 5 of 5 |
| During a 1.5 s zoom animation, to WARP | 5 of 5 |

So the rules for #274 are:

- Follow every rule above. The map uses one virtual surface written in 256 px tiles, never a plain
  surface near the texture limit, and never probes the device's limits at run time.
- Treat an `E_INVALIDARG` from a tile write as a defect to report, not a frame to retry.
- Test device recovery by what the screen shows, not by the redraw's success. Every white run here
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

The map found that step 3 is not enough for its virtual surfaces: it makes every surface again as
well. See [what landing #274 found](#what-landing-274-found).

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

## What landing #274 found

The map draws as the rules above describe: one `CompositionVirtualDrawingSurface` per drawing, each
written a region at a time through a staging `CanvasBitmap`, on a `SpriteVisual` that places the
drawing in the picture. The sprites sit in a container whose offset and scale follow a camera
property set by expression, and the outlines and the labels follow the same camera. Two things the spike did not show:

- **A surface made before a device replacement shows nothing written after it.** The spike saw
  surfaces made before the swap accept a redraw. In the map, after `SetCanvasDevice` and
  `RenderingDeviceReplaced`, every write into the old virtual surfaces succeeded and none of it
  appeared: the labels came back over an empty map, and stayed so. Making each surface again (same
  size, the same brush pointed at it) and then redrawing brought the picture back. Both builds were
  driven through the same forced replacement and compared on screen.
- **Never set `CompositionPathGeometry.Path` to null.** On Windows App SDK 1.8 it ended the process
  with an access violation inside the setter, on hovering a shape and leaving it. Not on every such
  call: once at the first, once many calls in. An empty path stands in for no outline.

Measured on a large real tree (Release, a 240 Hz display, a desktop GPU), before and after #274,
two runs of each:

| | Before | After |
| --- | --- | --- |
| Frame pacing in a zoom and a drag | 240 fps held, p95 interval about 4.3 ms | the same |
| Map work per zoom frame, 1080p | 0.074 / 0.072 ms | 0.071 / 0.057 ms |
| Map work per zoom frame, 4K | 0.077 / 0.101 ms | 0.053 / 0.052 ms |
| Worst zoom frame, 1080p | 11.6 / 8.8 ms | 5.5 / 4.3 ms |
| Worst zoom frame, 4K | 9.2 / 15.2 ms | 3.2 / 3.4 ms |
| Map work per drag frame | 0.040 / 0.056 ms | 0.041 / 0.034 ms |
| UI-thread CPU per zoom frame, 1080p | 0.374 / 0.373 ms | 0.413 / 0.332 ms |
| UI-thread CPU per zoom frame, 4K | 0.482 / 0.601 ms | 0.359 / 0.367 ms |
| Hand-over of one region | 0.03–0.06 ms, without the bitmap upload XAML ran later | 0.06–0.09 ms, the GPU write included |
| Working set while dragging, 4K | 1,373 / 1,444 MB | 1,245 / 1,302 MB |
| Private bytes while dragging, 4K | 1,491 / 1,537 MB | 1,414 / 1,467 MB |
| Dedicated GPU memory while dragging, 4K | 513 MB | 539 MB |

At 1080p the memory differences are within the run-to-run noise, because the scan tree dominates.
The longest stalls, 58–116 ms, came in both builds alike. At rest, 98% of samples at 1080p are
identical between the builds, one canvas pixel to one screen pixel.

## Still open

The Win2D package's licence, open here until #274, was cleared by the maintainer.

- **A monitor of a different scale.** Move the map between a 100% and a 150% monitor, and check the
  tiles stay one pixel to one pixel and redraw once.
- **Remote Desktop.** Record what `AreEffectsFast` reports in a remote session, and that the map
  falls back as described above.
- **ARM64.** Start the published ARM64 build on ARM64 hardware.
