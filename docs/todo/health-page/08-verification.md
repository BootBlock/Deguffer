# Stage 08: verification

> **Status:** 🟢 ACTIVE — not started. Prerequisites: stages 01 to 07.

## Done when

- [ ] `dotnet build Deguffer.sln` and `dotnet test Deguffer.sln` pass, read from their output.
- [ ] New tests bite: mutate a standing rule, the primary-signal rule, the once-only guard and the
      in-place update, and see the matching test fail.
- [ ] Driven, unelevated: the app starts and no drive is read before Health is selected. The first
      visit fills the page.
- [ ] Driven: leaving and returning keeps the selection and the rows without a second read,
      including a return while a read is running.
- [ ] Driven: Refresh updates in place.
- [ ] Driven, unelevated with a SATA drive: the banner and the button show.
- [ ] Driven, elevated: thresholds, rotation and link details show, and the banner does not.
- [ ] A captured image shows no serial number.
- [ ] This folder moves to `docs/todo/done/`, with its banners set to complete.
