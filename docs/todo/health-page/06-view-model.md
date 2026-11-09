# Stage 06: the view-model

> **Status:** 🟢 ACTIVE — not started. Prerequisites: stage 05.

## Touch points

| File | Change |
| --- | --- |
| `Deguffer.App/ViewModels/HealthDriveRow.cs` | New. One drive, written over in place, with its facts and attributes. |
| `Deguffer.App/ViewModels/HealthViewModel.cs` | New. |
| `Deguffer.App.Tests/HealthViewModelTests.cs` | New. |

## Design

`HealthViewModel` takes the reader's two phases as delegates, `isElevated`, the relaunch function,
`RunningActions` and `BusyWork`. The attribute rows bind `HealthAttribute` directly.

- `EnsureRead()` starts the first read and does nothing afterwards (H2).
- `RefreshAsync` abandons a running read and starts another, as `StartupViewModel` does. The
  command allows concurrent execution, so a stalled drive never disables Refresh.
- Phase one failing puts a sentence in the headline and leaves every row as it was.
- `CanElevate` is `ElevationOffer.ShouldOffer` over each row's latest report, so it does not
  flicker during a read. It drives both the banner and the button.

## Done when

- [ ] `EnsureRead` twice reads once.
- [ ] Refresh updates rows with no reset, and keeps the row objects and the selection.
- [ ] A superseded read's reports are ignored.
- [ ] A drive gone from phase one leaves, and a failed or cancelled read removes nothing.
- [ ] If the selected drive goes, the nearest row is selected.
- [ ] `RunningActions.MayEndProcess` stays true during a read.
- [ ] The offer follows the reports, and Elevate relaunches with `ElevationRequest.Health`.
