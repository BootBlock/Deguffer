# Stage 07: the page, the rail and the documents

> **Status:** 🟢 ACTIVE — not started. Prerequisites: stage 06.

## Touch points

| File | Change |
| --- | --- |
| `Deguffer.App/Views/HealthPage.xaml(.cs)` | New. Layout H14. `NavigationCacheMode.Required`. `Loaded` calls `EnsureRead`. |
| `Deguffer.App/Converters/DriveVerdictChipStyleConverter.cs` | New. Verdict to chip style. |
| `Deguffer.App/Converters/AttributeStandingChipStyleConverter.cs` | New, if the standings need their own chip words. |
| `Deguffer.App/App.xaml` | The converters. |
| `Deguffer.App/MainWindow.xaml` | `HealthItem`, after Installed apps. |
| `Deguffer.App/MainWindow.xaml.cs` | `HealthRequest` arm and `"Health"` arm. |
| `docs/todo/_spec.md` | §2 goal and non-goal, §7.6. |
| `README.md` | A Health section and the feature list line. |

## Design

The chip always states its word, so colour only reinforces it (§6.5). The drive list's selection
goes through `ListSelectionGuard`, because a cached return rebuilds containers and they report
selection changes the user did not make.

## Done when

- [ ] Both projects build.
- [ ] Spec §7.6 states what is read, what is never sent, and when elevation helps.
