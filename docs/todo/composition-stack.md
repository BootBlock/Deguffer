# The composition stack for the map — spike

> **Status:** 🟢 ACTIVE — the spike is finished and its answers below are what the
> `direct-composition` work (#294) builds on. Since #274 the Explore map draws through Win2D into
> composition surfaces: see [what landing it found](#what-landing-274-found). Since #276 its camera
> follows an `InteractionTracker`: see [what that found](#what-landing-276-found), measured from an origin since #307 ([what that found](#what-landing-307-found)), and zooms as deep as the detail in view since #306 ([what that found](#what-landing-306-found)). It flies into and out of folders on a spring since #278 ([what that found](#what-landing-278-found)). Its treemaps are shaded on the GPU where effects are fast since #284 ([what that found](#what-landing-284-found)), lit by the pointer there ([how](#the-pointers-light)). Since #290 the Memory page's map has the same camera, which follows what the reader zoomed into from one reading to the next ([what that found](#what-landing-290-found)). Since #283 a change of view carries the largest shapes from one view to the other ([what that found](#what-landing-283-found)). The maintainer has
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
  (#284), and no shadow under the shape the pointer is over or dimming round a selection (#281).
  Hover and selection keep their outlines, which are composition shapes over the picture, and the
  hatch over what a removal is acting on stays, because it is plain geometry. High contrast turns
  the shadow and the dimming off in the same way, and gives the outlines and the hatch the theme's
  colours. Cushions are painted on the CPU by `TileRasteriser`, rather than shaded on the GPU
  ([why](#what-landing-284-found)).
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

## What landing #276 found

The camera follows an `InteractionTracker`: the camera property set's scale and offset are
expressions of the tracker's, and the map's own visual is its `VisualInteractionSource`. Three things
the documentation does not say:

- **The tracker cannot zoom for a plain wheel.** With wheel redirection on and the wheel's position
  modes turned off, a plain turn of the wheel still reaches the UI thread's `PointerWheelChanged`;
  only Ctrl+wheel is zoomed on the compositor, by the tracker's own step and with inertia. So a plain
  wheel starts a glide from the UI thread, which the compositor then plays.
- **A position placed past the bounds stays on screen.** `TryUpdatePosition` with
  `InteractionTrackerClampingOption.Disabled` shows the picture past its edge, while every later
  report, and `Position` read on the UI thread, says it is at the edge. Neither a coast at no speed
  nor a custom animation back to the edge then moves the picture. The tracker is kept inside its
  bounds instead, and a drag past an edge is a stretch the camera holds and eases back itself.
- **A scroller takes the focus from a pressed element.** The page's `ScrollViewer` focuses itself on
  every press that bubbles to it, after the map's own handlers. The map takes the focus as the press
  is released, and marks the release handled; the page's Back and Forward buttons listen with
  `handledEventsToo`, and a click still raises `Tapped`.

Touch injected with `InjectTouchInput` reaches the tracker through `TryRedirectForManipulation`: a
pinch and a flick both coast and settle. Measured on `C:\Windows` (51.8 GB), a 240 Hz display,
Debug, one run of each, from DxgKrnl present events and the UI thread's CPU time, over a 140 ms
mouse drag and the 2.2 s after it:

| | Before | After |
| --- | --- | --- |
| Frame interval during the drag | about 16.6 ms, paced by the mouse | the same |
| After the release | the picture stops | a 1.0 s fling at the display's rate: mean about 4.2 ms, longest 5.5 ms |
| UI-thread CPU over the window | 31.2 ms | 78.1 ms |

The UI thread follows every report of the tracker while it coasts, about 0.2 ms each, so the readout
under the map and the hit test go on naming what the screen shows (§7.1). The picture itself moves
on the compositor, and no frame of the fling was late.

## What landing #307 found

The camera is measured from an origin (`MapOrigin`) so it stays exact past 64×, which #306 needs.

- **Only the corner moves, never the zoom.** The tracker's position counts from the origin and its scale stays the zoom, so moving the origin is one `TryUpdatePosition` at rest. A scale and a position asked for in the same frame can leave the position wrong, so a base zoom would have cost a second request.
- **The camera and the placements change in one UI commit, the tracker a frame later.** The camera is held at its value from the new origin while every placement is fitted again, then follows the tracker once the tracker reports the move. The tracker answers in the order it did things, so every report before its answer, a hand's included, is read from the old origin (`MapOriginMove`). Only the answer resolves the move. If a hand holds the camera, the tracker refuses the request and everything goes back to the old origin. A turn of the wheel or the touchpad only coasts the tracker, so the request is carried out, and stops the coast where it went.
- **The tracker's bounds take in both origins while the move is unanswered.** The bounds and the request reach the compositor separately, and a tracker outside its bounds springs back by itself.
- **The far bound is computed on the CPU in double precision** (`1 - origin`) and stored in the camera's property set, because near the far edge it is small and the expression multiplies it by the scale.
- **Tiles more than 64 canvases off a drawing are cut** before they become single precision. At 64× or less, no tile is cut.
- **Driven** on a build with the ceiling raised to 1,024× and a temporary log: the origin moved at rest at 64× and the tracker answered within about 10 ms. Outlines and names stayed on their shapes, and a click at 1,024× selected the shape under the pointer.
- **Left for #306:** the dimming rectangle in `MapGraphics` has a fixed extent of 2^20 canvas pixels, which is enough for a drawing shown zoomed out by up to 64×. #306 made it reach as far as each drawing's picture does ([below](#what-landing-306-found)).

## What landing #306 found

The zoom's ceiling follows the detail in view (`MapCeiling`) rather than a fixed 64×.

- **The layout says what it could not draw.** A block standing in for items too small to draw, and a folder drawn as one block, carry how wide their smallest hidden item would be (`ExploreTile.Finest`). A small folder opens with no frame at all, so a folder drawn as one block is in practice one past the depth its zoom opens.
- **The rule needs a floor of one level of detail per drawing.** Zooming until a hidden block covers the screen got stuck once it did while its items were still too small; an estimate from the smallest item's size alone falls short where the item is a sliver. Each drawing therefore allows at least twice its zoom while anything in view is undrawn.
- **Stopping where everything is drawn left the files unnamed.** Every one-byte file drawn was about three pixels wide. The ceiling then goes on to where the smallest shape in view has room for its name, with a pixel to spare: aimed at the threshold exactly, equal neighbours fell either side of it by a rounding error and half of them went unnamed.
- **Every limit reads the one ceiling:** each zoom the map asks for, the tracker's `MaxScale`, and the camera's clamp, which reads it from the property set. A place returned to from history raises it, and a lower one never cuts a zoom already made.
- **A request counts as arrived by its distance on screen.** As a fraction of the picture, a whole screen is a rounding error at a deep zoom.
- **The dimming round a selection reaches twice the drawing's picture** at the drawing's zoom, in place of the fixed 2^20 pixels.
- **Driven** into 20,000 one-byte files past 64×: they were drawn one by one, each named, the zoom stopped there, and a hover and a click resolved to the shape under the pointer. Zooming out from there showed no seam, and the dimming covered the picture throughout.

## What landing #278 found

Every change of folder on a treemap of one tree is one flight of the map's camera, in or out, however many levels it crosses (`FolderSteps`, `MapDescent`).

- **The spring is Core's, not the compositor's.** A `Vector3NaturalMotionAnimation` on a property set cannot be read back, so a flight turned round part of the way would not know where it was or how fast it went. A critically damped `Spring` in closed form, clocked on the UI thread as the old opening was, knows both exactly, and carries its speed into the turn. Critically damped, because a spring that passed its end would show the screen's edge bare.
- **The tracker cannot fly a folder.** Its scale is one factor on both axes, and a folder's own picture fills the screen on both, so the outer picture has to stretch by more on one axis than the other. The flight carries the two sets of drawings, as the opening did, and hands the inner one to the tracker's camera exactly at the end.
- **The flight zooms about the point both ends agree on, by equal ratios,** on each axis apart, as `MapViewport.Between` does for a glide. A straight line between the frames slid the outer picture sideways, and a flight several levels deep lurched at one end.
- **A step out waits for the picture it goes out to.** The shape to pull back into is only known once that picture is laid out, and the picture left covers the screen until then.
- **A folder too deep to be drawn is flown to from the deepest folder above it that is**, which is where the screen shows it (`ExploreSurface.FrameOf`).
- **The direction comes from the tree**, so Back, Forward, Up, a breadcrumb and a double-click fly alike. The double-click no longer hands the map a shape.
- **With animation effects off the folder fades in place,** as `MotionToken.Entrance` already says, rather than cutting.
- **Driven** on a fixture of nested folders: in one level and two at once, back out to the root in one pull-back, a flight turned round 70 ms in, and the same with animation effects off. A click after each picked the shape under the pointer.

## What landing #284 found

A treemap's canvas, and each tile of a moving one, is shaded on the GPU in one pass where
`AreEffectsFast()` is true and Win2D's device is a hardware one that runs Direct3D feature level 10
(`MapGraphics.Shades`).
Everywhere else, and for the sunburst, whose sectors are not rectangles, the CPU paints it as before.
Both routes shade the same `CushionQuads`, so they cannot disagree about a shape's edges, colour or
ridge, and a test shades a tree on WARP and holds the result to the CPU's: every channel within one
level, and fewer than one pixel in a thousand differing at all.

- **The cost is in handing the GPU its shapes, not in shading them.** Win2D draws one rectangle per
  call, and a large tree is 30,000 to 60,000 rectangles. Recording them took 20 to 40 ms a canvas
  with `FillRectangle` and with a sprite batch alike, which is slower than the CPU's whole picture,
  so a Win2D `PixelShaderEffect`, as the issue first proposed, loses before it shades a pixel.
- **So the quads go up in one copy and are drawn by one instanced call**, through Direct3D 11 on the
  device under Win2D's (`CushionRenderer`). The GPU keeps the order of the primitives of one draw,
  so a later quad covers an earlier one, which is the rasteriser's picture. The result is copied
  into the map's surface by Win2D, inside the device's lock.
- **No new package.** The interop is a few `ComImport` interfaces (`Direct3D`), and the shader is
  HLSL in the App, compiled once per process by the `d3dcompiler_47` every Windows 10 and 11
  carries. `ComputeSharp.D2D1.WinUI` (MIT, IL only, so x86 works) was vetted and not
  taken: it needs the App on the 10.0.22621 SDK, its last release is April 2025 with upstream tests
  on x64 and ARM64 only, and it would compile a Win2D effect, which is the slow route above.
- **Direct3D 11 refuses a box on a constant buffer, and drops the update without an error.** Every
  quad was then drawn at a size of zero, and only the ground showed.
- **A `ComImport` interface passes an array as a `SAFEARRAY`** unless it is marked as a C array, and
  the device refuses that as an invalid argument.
- **The overview is shrunk from the shaded canvas**, straight after the shading, so a shaded picture
  is never copied back to the CPU.
- **Driven** on `C:\Windows` (51.8 GB): the treemap arrived whole, zooming kept it sharp, the outline
  and the readout named the shape under the pointer, the overview showed, and the sunburst and the
  way back to the treemap worked. `d3dcompiler_47` was loaded in the process, which happens only when
  the renderer compiles its shaders.

Measured on `C:\Windows`, 306,675 nodes, laid out once per size, a release build, 15 runs each and
the median, on a desktop with a 32-thread CPU, a discrete GPU and the CPU's integrated GPU. The
machine was shared with other work, so the CPU figures moved by a few milliseconds between runs.

| A whole canvas | 1080p, 32,114 quads | 4K, 62,786 quads |
| --- | --- | --- |
| CPU, 31 threads, as the map paints it | 15.5–17.3 ms | 15.5–25.0 ms |
| CPU, one thread | 36.3 ms | 113.7 ms |
| Uploading the CPU's canvas, discrete / integrated / WARP | 0.9 / 3.3 / 1.2–1.9 ms | 2.5–3.0 / 11.4–12.3 / 4.0–4.2 ms |
| Win2D, one `FillRectangle` per quad, discrete GPU | 34.5 ms | 37.0 ms |
| Win2D, a sprite batch, discrete / integrated / WARP | 20.8–21.7 / 20.4–20.5 / 26.3–27.8 ms | 28.5–44.3 / 53.4–55.7 / 66.1–67.9 ms |
| Direct3D 11 instanced, discrete GPU | **0.39 ms** | **0.68 ms** |
| Direct3D 11 instanced, integrated GPU | **2.19 ms** | **6.20 ms** |
| Direct3D 11 instanced, WARP | 7.42 ms | 19.40 ms |

Each GPU figure runs from the quads to a finished canvas read back, so it includes building the quads
on the CPU (0.2 to 0.6 ms) and waiting for the GPU. Every route's picture matched the CPU's to within
one level a channel, with 36 to 192 pixels of a canvas one level apart.

WARP is no faster than the CPU at 4K and would shade on the UI thread, so a software device keeps the
CPU's route, a region at a time and off the UI thread. The map asks Win2D's device itself, by its
adapter's software flag (`CushionRenderer.OnSoftware`), rather than trust `AreEffectsFast()`, which
speaks for the compositor's device, a separate one. What `AreEffectsFast()` reports on WARP and in a
remote session was not observed here.

### The pointer's light

A soft point light follows the pointer over a treemap shaded on the GPU (`PointerLight`): 160 DIPs
of reach, half its reach above the canvas, adding at most 35% of a shape's colour where a cushion
faces it. A slope facing the pointer catches more of it than one facing away, so the shape under the
pointer shows by its relief, beside the outline. It is added in the cushion shader, and fades to
nothing at its reach, so past it the canvas is the picture without a light, pixel for pixel. Only the
drawing at rest on screen is lit: not a tile while the map moves, not a drawing on its way in, not a
canvas the CPU painted, and not in high contrast (§6.5).

- **Only what the light touched is shaded again.** A move shades one rectangle round where the light
  was and where it is (`PointerLight.Between`), from the quads already on the GPU, which are not sent
  again. The vertex shader clamps each quad to that rectangle, so the pixel work is the rectangle's.
  The vertex work is the whole canvas's quads, so a move costs no more than shading the canvas whole,
  which the table above puts at 0.39 to 0.68 ms on the discrete GPU and 2.2 to 6.2 ms on the
  integrated one. The light's own pass was not timed apart from that.
- **Once a commit, and never in the commit that shaded the canvas whole.** A part of a virtual
  surface written in the same commit as the whole of it left the rest of the surface empty on screen:
  after a zoom, the new drawing was shaded whole and lit at once, and only the rectangle round the
  pointer showed. The map without the light did not do it, nor did a light shaded 100 ms later. So a layer keeps
  where the light is wanted and shades it after `Compositor.RequestCommitAsync` completes, and a
  whole shading asks for its own commit, which supersedes any asked before it. A pointer moves
  several times a frame, so this also shades the light once a commit rather than once a move.
- **Not a composition `PointLight`.** A composition light lights a visual as a flat surface unless an
  effect brush gives it a normal map (`SceneLightingEffect`). For the light to follow the cushions'
  slopes, each drawing would need a second canvas of the same size holding the cushions' normals,
  shaded alongside it, and an effect brush per layer that samples both, on every frame the light
  moves. The cushion shader already has each pixel's normal, so the light costs one constant buffer
  and a region pass. The composition light was therefore not built or measured.
- **Driven** on `C:\Windows`: the light followed a smooth pointer move and left no trace behind it,
  went when the pointer left the map, and after five wheel notches in, the drawing at rest was whole
  and lit at the pointer.

## What landing #290 found

The Memory page's map now has Explore's camera, and each reading of memory moves the map from the
last layout to the new one, as a scan's snapshots do (#310). Its readings are treemaps, so this was
the first time that move ran on a zoomed picture, and on a canvas shaded on the GPU.

- **The camera follows the shape at the middle of the screen** (`MapFollow`). A treemap ordered by
  size moves a process that grew past a sibling to the other side of its parent, so a camera held
  where it was in the picture showed whatever moved into that place. The point under the middle is
  kept at the same share of the deepest shape it was over, found again in the new reading by what it
  is, and the camera moves with it over the same 250 ms as the shapes. The zoom is the reader's and
  never changes. A shape that went hands over to the folder it was in.
- **A zoomed drawing holds only what reaches the canvas**, so the shape followed was not in the new
  drawing once a reordering sent it a few screens away. `TreemapLayout.Locate` lays out only the
  folders on the path to one shape, as the whole picture lays them out, and places it exactly, on
  the canvas or off it.
- **A drawing landed empty but for the light's rectangle, or showed the whole picture at the wrong
  size, on about half the readings.** The renderer shades every canvas into one render target
  of its own and Win2D copies it into the layer's surface. Ending the drawing session left that copy
  queued in Direct2D, and Direct2D cannot see the renderer write its target through Direct3D, so
  the next shading overwrote what the copy read before it ran. In the same few milliseconds a reading
  shades the new drawing whole, shades the light off the drawing it replaces, and shades the whole
  picture under it, so the new layer received one of the other two. Each session that reads the
  renderer's target now flushes before it ends (`MapGraphics.Shade`, `Shrink`). Measured on a zoomed
  Memory map: 13 of 24 captures dark before, none of 16 after.
- **This is also what #284 met** when a drawing shaded whole and lit at once showed only the
  rectangle round the pointer ([above](#the-pointers-light)). Waiting for a commit before shading the
  light hid it for one layer, as the queued copy had run by then. It did not cover two layers, which
  is what a reading does. The light still waits for a commit, which shades it once a frame rather
  than once a pointer move.
- **Driven** on a machine's live memory: zoomed in, readings moved the shapes and kept the zoom,
  the overview and the light; zoomed onto one process, the camera stayed on it through five readings.

## What landing #283 found

A change between the treemap, the icicle and the sunburst carries the 200 largest shapes both views
draw from one view's shape to the other's, while the two drawings crossfade under them for 450 ms
(`ViewMorph`). This is the first use of `PathKeyFrameAnimation` here.

- **The compositor blends two Win2D paths point by point when their figures match.** Each shape is
  one closed figure of the same 194 points in every view (`MorphOutline`): along the edge nearest the
  root, then back along the far edge. An icicle's bar becomes its wedge without turning over, because
  its top is the wedge's inner arc and its left is where the wedge starts.
- **The shapes are flat and the drawings are shaded,** so the shapes fade in over the first fifth of
  the move and out over the last fifth. Without that, the picture would flatten as the move starts,
  and the shading would flash on as it lands.
- **A press during the move lands it and picks nothing,** because no shape is where either view
  draws it. A zoomed treemap changes view with a crossfade only, as its shapes on screen are not
  where the whole treemap draws them. With animation effects off the views swap at once.
- **Driven** on a synthetic folder of 80 files in 25 folders: treemap to icicle, icicle to sunburst
  and sunburst to treemap each showed 22 to 26 distinct frames over about 460 ms, and three with
  animation effects off.

## Still open

The Win2D package's licence, open here until #274, was cleared by the maintainer.

- **A monitor of a different scale.** Move the map between a 100% and a 150% monitor, and check the
  tiles stay one pixel to one pixel and redraw once.
- **Remote Desktop.** Record what `AreEffectsFast` reports in a remote session, and that the map
  falls back as described above.
- **ARM64.** Start the published ARM64 build on ARM64 hardware.
