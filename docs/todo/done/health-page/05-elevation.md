# Stage 05: elevation

> **Status:** ✅ COMPLETE — 2026-11-09. Prerequisites: stage 04.

## Touch points

| File | Change |
| --- | --- |
| `Deguffer.Core/Execution/ElevationRequest.cs` | `Health` request, `--health` switch, parsed in `From` after Startup. |
| `Deguffer.Core/Execution/ElevationOffer.cs` | `ShouldOffer(bool, IEnumerable<DriveHealth>)`. |
| `Deguffer.Core.Tests/ElevationRequestTests.cs` | The pinned switch and the precedence. |

## Done when

- [x] `ToArguments` is exactly `--health`, and it round-trips through `From`.
- [x] `--health` with `--startup` resolves as `From` states, and a test says so.
- [x] The offer is false elevated, false where no drive was refused, and true otherwise.
