# Stage 08: verification

> **Status:** 🟢 ACTIVE — open: the unticked items below have not been made. Prerequisites: stages 01 to 07.

## Done when

- [x] `dotnet build Deguffer.sln` and `dotnet test Deguffer.sln` pass, read from their output.
- [x] New tests bite: mutate a standing rule, the primary-signal rule, the once-only guard and the
      in-place update, and see the matching test fail.
- [x] Driven, unelevated: the app starts and no drive is read before Health is selected. The first
      visit fills the page.
- [x] Driven: leaving and returning keeps the selection and the rows. `EnsureRead`'s test excludes a
      second read.
- [ ] Driven: a return while a read is running. A read here takes milliseconds, so it could not be
      caught in the act. `ASupersededReadsReportsAreIgnored` covers the wiring.
- [x] Driven: Refresh keeps the drives and the selection. `ARefreshKeepsTheRowsAndTheSelectionWithNoReset`
      asserts the in-place update by its change notifications.
- [x] Driven, unelevated with a SATA drive: the banner and the button show.
- [ ] Driven, elevated: thresholds, rotation and link details show, and the banner does not.
- [x] A captured image shows no serial number.
- [ ] This folder moves to `docs/todo/done/`, with its banners set to complete.
