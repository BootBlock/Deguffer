# Stage 05: elevation

> **Status:** 🟢 ACTIVE — not started. Prerequisites: stage 04.

## Touch points

| File | Change |
| --- | --- |
| `Deguffer.Core/Execution/ElevationRequest.cs` | `Health` request, `--health` switch, parsed in `From` after Startup. |
| `Deguffer.Core/Execution/ElevationOffer.cs` | `ShouldOffer(bool, IEnumerable<DriveHealth>)`. |
| `Deguffer.Core.Tests/ElevationRequestTests.cs` | The pinned switch and the precedence. |

## Done when

- [ ] `ToArguments` is exactly `--health`, and it round-trips through `From`.
- [ ] `--health` with `--startup` resolves as `From` states, and a test says so.
- [ ] The offer is false elevated, false where no drive was refused, and true otherwise.
