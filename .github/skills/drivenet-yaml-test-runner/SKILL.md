---
name: drivenet-yaml-test-runner
description: "Use this skill to create or review Drive.NET YAML test runner manifests and suites for deterministic multi-step desktop automation. Covers suite structure, tool selection, comments, expectations, save JSON paths, agent-friendly diagnostics, Companion examples, pointer-button control via mouseDown and mouseUp, and structured `test --result-json` output. Keywords: Drive.NET, YAML test runner, manifest, suite, UI test script, test automation, save path, result-json, comments, companion, agentic AI, deterministic workflow, mouseDown, mouseUp."
argument-hint: "[target app] [goal] [selectors or flow]"
user-invocable: true
---

# Drive.NET YAML Test Runner

Use this skill when you need a reusable YAML manifest or suite instead of an ad hoc stream of individual CLI commands.

## When To Prefer YAML

- The workflow has more than a few steps.
- An agent will need to rerun the same flow after code changes.
- You want expectations and failure messages embedded with the steps.
- You want machine-readable output from one run via `test --result-json`.

## Authoring Rules

- Split large flows into focused suites. One suite per behavioral area keeps failures local.
- Keep test cases that depend on one interactive app state, one login, or one warning-covered session in the same suite. Suite boundaries disconnect sessions and may relaunch or reattach the target.
- Prefer `automation_id` selectors over broad `name` or `control_type` searches.
- Add comments that explain intent, not syntax.
- Save IDs and useful fields explicitly instead of forcing later steps to rediscover them.
- Use `query properties` or `query tree` before risky interactions if an agent may need extra context later.
- Use `wait_for` on concrete `element_id` values for state checks like `elementEnabled` and `textEquals`.
- Use suite-level `helpers` when the app under test must discover, connect to, or otherwise react to a second process.
- For apps that should behave like singletons during testing, prefer `app.single_instance: restart` with both `process_name` and `exe` so orphaned instances are closed before the suite runs.
- For apps that persist state under `%LOCALAPPDATA%` or another user profile root, prefer a disposable `app.environment` override so the suite never mutates the operator's real profile.
- If the target app exposes a test-only multi-instance switch, pass it through `app.args` instead of using `single_instance: restart` when you want the suite to coexist with an already-running user instance.
- For non-traditional desktop surfaces, call out `attach_mode` explicitly: `processVisibleWindow` for the launched or specified process, `anyVisibleProcessWindow` for any visible process in the family, or `newestVisibleWindow` for the newest visible family member.
- For launched `app` or `helpers`, use `session_start_warning: false` only when the caller already provided its own user-facing warning. Do not use it just to hide repeated same-manifest warnings; the runner now suppresses duplicate warnings for the same target within one run.
- Use `session_start_warning_mode: persistent` when the suite should keep a bottom-right warning card visible until the runner disconnects the session or tears down the launched process.
- Always recommend `--result-json` for agent-driven runs.
- For VS Code extension testing, use `attach_only: true` (or omit `exe`) with `process_name: Code` and `window_title_regex: '\[Extension Development Host\]'` after launching the host via F5. Do not use `lifecycle launch` or `app.exe` for VS Code — Electron's singleton mutex blocks a second instance. End the manifest with a cleanup suite that conditionally dismisses any save-changes dialog before closing the host window.
- When running `DriveNet.Cli.exe test` from a terminal, ensure no other Drive.NET session (MCP, CLI, or IDE) is attached to the same target PID. The single-controller attach guard fails with *"already connected by another Drive.NET MCP/CLI controller"* if two controllers target the same process.
- Avoid assertions that depend on exact `TreeItem` counts in live process trees; ancestor chains and helper shells can vary between environments while still representing the same target.
- For status-style text in templated desktop UI, assert against a stable accessible name or a downstream enabled-state change instead of assuming `query properties` will expose the rendered text value.

## Suite Lifecycle Blocks

Each suite supports four optional blocks that execute in order:

1. `setup` — runs before any test cases. If any setup step fails, the suite aborts directly to `finally`/`cleanup`.
2. `tests` — ordered test cases (required).
3. `teardown` — runs after test cases when setup succeeded and a session is available.
4. `finally` (or `cleanup`) — **always** runs before session/process cleanup, even when setup fails. Use only one of `finally` or `cleanup`; they are synonyms.

```yaml
setup:
  - tool: query
    action: find
    args:
      automation_id: ReadyIndicator
    expect:
      success: true

tests:
  - name: my test case
    steps:
      - tool: query
        action: find
        args:
          automation_id: SubmitButton
        save:
          submit_button: $.items[0].elementId
      - tool: interact
        action: click
        args:
          element_id: "${submit_button}"

teardown:
  - tool: capture

finally:
  - tool: window
    action: close
```

## App Block Configuration

The `app` block accepts:

| Field | Description |
|---|---|
| `exe` | Path to executable to launch. |
| `args` | Command-line arguments for the launched process. |
| `environment` | Optional environment variables injected into the launched process. Use for hermetic app-data or profile overrides. |
| `process_name` | Attach to a running process by name instead of launching. |
| `process_id` | Attach to a running process by PID. |
| `single_instance` | Instance policy: `reuse` (reuse existing) or `restart` (close existing first). |
| `startup_wait_ms` | Wait for the app to become idle after launch (0–300000). |
| `window_title_regex` | Regex filter for window title during session attach (useful when a process has multiple windows). |
| `connect_retry_ms` | Retry budget in ms for session attach. Useful for non-traditional surfaces like appbars or topmost windows that appear shortly after launch. |
| `attach_mode` | Visible window attach policy: `processVisibleWindow` (default), `anyVisibleProcessWindow`, or `newestVisibleWindow`. |
| `session_start_warning` | Override the pre-launch safety warning (default: true). |
| `session_start_warning_mode` | Warning display mode: `toast` or `persistent`. Providing a mode also enables the warning unless `session_start_warning: false` is set. |

### Helpers

The `helpers` list launches supporting apps before the suite. Each helper uses the same fields as `app`, plus:

| Field | Description |
|---|---|
| `connect` | When `true`, connects an automation session to the helper and seeds `helper{N}SessionId` for cross-session steps. Helper-only; do not use on the main `app`. |

## Seeded Variables

After app and helper preparation, the runner seeds these variables:

**App variables:** `appProcessId`, `appProcessName`, `appWindowHandle`, `appWindowHandleHex`, `appWindowTitle`, `appSessionId`, `appSingleInstance`, `appMatchedInstanceCount`, `appClosedInstanceCount`, `appReusedExistingInstance`.

**Helper variables** (numbered `helper1`, `helper2`, etc.): `helper{N}ProcessId`, `helper{N}ProcessName`, `helper{N}WindowHandle`, `helper{N}WindowHandleHex`, `helper{N}WindowTitle`, `helper{N}SingleInstance`, `helper{N}MatchedInstanceCount`, `helper{N}ClosedInstanceCount`, `helper{N}ReusedExistingInstance`, `helper{N}SessionId` (only when `connect: true`).

**Execution variables** (updated after every step): `lastStepIndex`, `lastStepSuccess`, `lastStepSkipped`, `lastStepError`, `step{N}Success`, `step{N}Skipped`, `step{N}Error`.

## Supported YAML Step Tools

- `desktop` — actions: `monitors`, `foregroundWindow`.
- `discover` — args: `filter`, `dotnet_only`, `include_windows`, `hierarchical`.
- `query` — actions: `find`, `resolve`, `explain`, `tree`, `properties`, `bounds`, `children`, `parent`, `gridData`.
- `interact` — actions: `click`, `doubleClick`, `rightClick`, `type`, `clear`, `sendKeys`, `select`, `toggle`, `expand`, `collapse`, `scrollIntoView`, `dragTo`, `mouseDown`, `mouseUp`, `hover`, `mouseMove`, `moveTo`, `setFocus`, `highlight`, `clipboard`, `pointerPath`.
- `wait_for` — conditions: `elementExists`, `elementRemoved`, `propertyChanged`, `textEquals`, `elementEnabled`, `elementVisible`, `windowOpened`, `windowClosed`, `structureChanged`, `childAppeared`, `childRemoved`, `helpTextEquals`, `helpTextContains`, `itemStatusEquals`, `itemStatusContains`.
- `window` — actions: `list`, `minimize`, `maximize`, `restore`, `bringToFront`, `close`. Optional `window_handle` arg.
- `capture` — optional `output` arg (must end in `.png`). Window captures automatically trim invisible non-client resize borders when DWM reports a tighter visible frame.

The YAML runner does not support nested `batch` steps. Suites already provide ordered execution, shared variables, and expectations.

For `query`, `interact`, and `wait_for`, YAML uses snake_case args that map to the batch/MCP camelCase fields. That includes `session_id` -> `sessionId`, `element_id` -> `elementId`, and pointer fields such as `destination_x`, `destination_y`, `source_x`, `mouse_button`, `duration_ms`, `hover_mode`, `approach_from`, `velocity_ms`, `motion_profile`, and `motion_exaggeration`. `mouseDown` and `mouseUp` use the same `destination_x`, `destination_y`, and `mouse_button` mappings, and default `mouse_button` to `left` when omitted.

For held-button pickers, prefer resolving a stable helper-side target first, then save `query bounds` clickable-point coordinates and feed those exact values into `mouseDown` and `mouseUp`. The Companion picker example demonstrates this end-to-end pattern.

Generic held-button drags are validated on plain WinUI pointer surfaces, but popup-hosted flyouts can still apply app-specific release or capture logic on `mouseUp`. For popup drags, add follow-up `window list` or popup-rooted `query` steps after release instead of assuming the held drag persisted.

For `wait_for`, state conditions such as `elementEnabled`, `elementVisible`, `textEquals`, and `propertyChanged` require `args.element_id`. If the UI replaces the element instance during a refresh, prefer a selector-based `query find` or `query resolve` step with `retry` so the suite reacquires the live element instead of polling a stale id.

`wait_for` steps support `save` to capture result fields into variables, for example capturing the handle of a newly opened popup window:

```yaml
- tool: wait_for
  args:
    condition: windowOpened
    owner_handle: "${main_window}"
  save:
    popup_handle: $.changedWindow.windowHandle
```

The `owner_handle` arg filters `windowOpened`/`windowClosed` to popup or flyout windows owned by the specified parent handle.

### Cross-Session Step Targeting

When a helper uses `connect: true`, later `query`, `interact`, and `wait_for` steps can target that helper session explicitly:

```yaml
helpers:
  - exe: tests/DriveNet.TestApp.WinForms/bin/Release/net10.0-windows/DriveNet.TestApp.WinForms.exe
    connect: true
    startup_wait_ms: 5000

tests:
  - name: helper resolve
    steps:
      - tool: query
        action: resolve
        comment: Resolve the helper-side control through the connected helper session.
        args:
          session_id: "${helper1SessionId}"
          automation_id: MainNavigation
        expect:
          success: true
```

Use `${appSessionId}` when you need to switch an explicit later step back to the primary app session.

## Step-Level Fields

| Field | Description |
|---|---|
| `tool` | Step tool name (required). |
| `action` | Tool action (required for `query`, `interact`, `window`, `desktop`). |
| `args` | Tool-specific arguments. |
| `expect` | Assertion block (see Expectations below). |
| `save` | JSON path extractions into named variables. |
| `when` | Conditional execution (see Conditional Execution below). |
| `retry` | Retry policy: `max_attempts`, `delay_ms`, `backoff_ms`. |
| `delay_before_ms` | Milliseconds to wait before executing the step (0–60000). |
| `delay_after_ms` | Milliseconds to wait after the step completes (0–60000). |
| `continue_on_failure` | When `true`, the suite continues after this step fails. Use for cleanup or diagnostic capture steps. |
| `comment` | Free-text annotation for intent or context. |

## Expectations

The `expect` block supports these assertion types:

| Assertion | Type | Description |
|---|---|---|
| `success` | bool | Whether the step command succeeded. |
| `count` | int | Exact element/item count. |
| `count_gt` | int | Count greater than. |
| `count_gte` | int | Count greater than or equal. |
| `exists` | bool | Whether any elements exist (count > 0). |
| `contains` | string | Substring match in the result. |
| `not_contains` | string | Substring must NOT appear in the result. |
| `property` | string | Property name to check (combine with `equals`, `value_gt`, etc.). Supports **dot-notation** for nested paths, e.g. `boundingRect.left`. |
| `equals` | string | Property value equality (used with `property`). |
| `value_gt` | double | Numeric property greater than. |
| `value_gte` | double | Numeric property greater or equal. |
| `value_lt` | double | Numeric property less than. |
| `value_lte` | double | Numeric property less or equal. |
| `condition_met` | bool | Whether a `wait_for` condition was met. |
| `file_exists` | bool | Whether a file exists at the result path. |

## Conditional Execution

The `when` block controls whether a step runs:

| Field | Description |
|---|---|
| `variable` | Variable name to check (required). |
| `equals` | Run only if the variable equals this value. |
| `not_equals` | Run only if the variable differs from this value. |
| `exists` | Run only if the variable exists (`true`) or does not exist (`false`). |

## Retry Policy

The `retry` block on a step controls automatic retries:

| Field | Description |
|---|---|
| `max_attempts` | Total attempts allowed (default: 1, meaning no retry). |
| `delay_ms` | Fixed delay in ms between the first and second attempt. |
| `backoff_ms` | Additional delay multiplied by `(attempt - 2)` for attempts 3+. |

## Minimal Templates

Manifest:

```yaml
name: My App Tests
suites:
  - querying.yaml
  - navigation.yaml
```

Suite:

```yaml
name: My App Querying
app:
  exe: path/to/MyApp.exe
  process_name: MyApp
  single_instance: restart
  startup_wait_ms: 5000
  session_start_warning: true
  session_start_warning_mode: persistent

tests:
  - name: find a stable control
    steps:
      - tool: query
        action: find
        args:
          automation_id: StableControlId
        expect:
          success: true
          count: 1
        save:
          controlId: $.items[0].elementId
```

## Save Path Cheat Sheet

- `desktop foregroundWindow`: `$.foregroundWindow.title`
- `desktop monitors`: `$.items[0].deviceName`
- `query find`: `$.items[0].elementId`
- `window list`: `$.items[0].handle` or `$.items[0].width`
- `discover`: `$.items[0].processName`
- `capture`: `$.filePath`

For README assets shown on GitHub, prefer `shadow: false` unless you verify the rendered page. GitHub dark theme can make a dark drop shadow read like a heavy border after inline scaling.
- `query children`: `$[0].elementId`
- `query tree`: inspect the object first; it is not wrapped in `{ items, count }`

## Agent-Friendly Output Pattern

Run with:

```powershell
dotnet run --project src/DriveNet.Cli -- test --manifest path/to/manifest.yaml --result-json .\artifacts\test-results.json
```

Tell the agent to inspect:

- suite/test summaries
- per-step `result`
- per-step `saved`
- `commandSuccess` versus `passed`
- `failureReason` and `commandError`

## Launch Warning Behavior

- When a suite launches an `app.exe` or helper `exe`, the runner shows the default 1-second `toast` warning before `Process.Start` unless that app block sets `session_start_warning: false`.
- When that app or helper uses `session_start_warning_mode: persistent`, the runner keeps the same bottom-right warning card visible until it disconnects the session or tears down the launched process.
- If that pre-launch warning was already shown, the runner suppresses the immediate duplicate session-connect warning for that launched target.
- Within one `test --manifest` run, the runner treats the first warning for a given app or helper target as covering later suites that hit the same process family or executable path, so it does not keep re-showing the same warning toast.
- Attaching to an already-running process still shows the first session-connect warning for that target in the run, then suppresses repeats for later suites that connect to the same target.
- The main `app` block always connects automatically. `connect: true` is helper-only and should not be used on `app`.

## Repository Examples

- [docs/yaml-test-runner.md](../../../docs/yaml-test-runner.md)
- [tests/definitions/companion/manifest.yaml](../../../tests/definitions/companion/manifest.yaml)
- [tests/definitions/companion/querying.yaml](../../../tests/definitions/companion/querying.yaml)
- [tests/definitions/companion/navigation.yaml](../../../tests/definitions/companion/navigation.yaml)
- [tests/definitions/companion/connected.yaml](../../../tests/definitions/companion/connected.yaml)
- [tests/definitions/companion/picker.yaml](../../../tests/definitions/companion/picker.yaml)
- [tests/definitions/companion/evidence.yaml](../../../tests/definitions/companion/evidence.yaml)

## VS Code Extension Testing

VS Code and Electron apps enforce a platform-level singleton mutex, so `lifecycle launch` and `app.exe` cannot create a usable isolated instance. The deterministic pattern is:

1. Open the extension workspace in VS Code and press **F5** to spawn the Extension Development Host.
2. Use `attach_only: true` with `process_name: Code` and `window_title_regex: '\[Extension Development Host\]'` in every suite.
3. End the manifest with a cleanup suite that closes the host and conditionally handles the save-changes dialog.

```yaml
app:
  process_name: Code
  window_title_regex: '\[Extension Development Host\]'
  attach_only: true
  single_instance: reuse
```

See [docs/yaml-test-runner.md — Testing VS Code / Electron Extensions](../../../docs/yaml-test-runner.md#testing-vs-code--electron-extensions) for a full cleanup suite example.