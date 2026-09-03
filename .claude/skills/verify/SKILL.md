---
name: verify
description: Drive the Deguffer WinUI app to confirm a change works, for the G8 bar in CLAUDE.md.
---

# Verifying a change in Deguffer

`dotnet build` and `dotnet test` are the floor, not the ceiling. They do not exercise the WinUI
shell, the preview-first flow, the elevation offer, or a real subprocess. G8 in
[CLAUDE.md](../../../CLAUDE.md#g8-what-verified-means) requires that a change with a runtime
surface is **observed**, and this skill is how you observe it.

Deguffer deletes directories. A shell defect that shows the wrong path, or an execute button that
runs a plan the preview did not describe, has a cost no unit test measures.

## What to drive, and what not to

Drive the app when the change touches the shell, a view-model, XAML, the preview-to-execute flow,
the elevation offer, or a real subprocess.

Do **not** drive the app to test a safety rule. The tier model, the §5.6 negative assertion and the
`LongPath` handling are all provable through `FakeUserEnvironment` and the `IProcessRunner` and
`IProcessInspector` seams, with no package manager installed. A test that only passes on a machine
with the real tool present does not test the safety rule, so a driven run is not a substitute for
one.

## The handle: Drive.NET

The repository's `.github/skills/drivenet-*` skills document **MCP tools that are not registered**
with Claude Code. Use the CLI instead:

```
C:\Users\<user>\AppData\Local\DriveNet\DriveNet.Cli.exe
```

`DriveNet.Server.exe` sits beside it, if registering the MCP server ever becomes worthwhile.

## Traps, each of which has cost a session

- **Never pass `--single-instance restart`.** It matches on the process *name*, which spans
  worktrees, so it kills the Deguffer another agent is running, and theirs kills yours. Launch with
  `--executable-path` alone, and scope every later call by the returned PID.
- **Launching blocks past the CLI's own `--startup-wait-ms`.** The launch command routinely exceeds
  the 120-second Bash timeout and lands in the background while the app is in fact up. Poll
  `discover` for the PID rather than trusting the launch call to return.
- **A file or folder picker cannot be driven at all.** `FolderPicker` opens in a separate
  `PickerHost` broker process, and every Drive.NET command against that PID fails with "Could not
  get UI Automation element": `find`, `capture` and `sendKeys` alike. You can prove the picker
  *opens*, which is what verifies the `InitializeWithWindow` interop, but not that a selection
  round-trips. Cover the code behind the picker another way. Close the dialog with `Stop-Process` on
  the `PickerHost` PID, or it blocks the app.
- **Plan notes and step rows are two clicks away, inside a modal.** They live on the `Contents` tab
  of each row's information dialog, so `find` matches none of their text until you open it: in
  compact, `--action expand` the row's "More about …" button first, then click its
  `What is …?` `Hyperlink`, then click the `Contents` `TabItem`. Until then an assertion against a
  note reads as absent when it is merely unopened. The tab fills on demand, so it is empty for a
  moment after the click.
- **The dialog's own close button is `--automation-id CloseButton`.** Matching it by the name
  `Close` also matches the window's title-bar button, and picking that one shuts the app down
  mid-run — which reads as a crash rather than as a mis-click.
- **Nav items respond to `--action click`.** Clicking the `Settings` and `Storage` `ListItem`s
  navigates correctly. If a click really does nothing, fall back to `--action setFocus` and then
  `--action sendKeys --keys Enter`. The flag is `--keys`, not `--value`, and the value is a bare
  `Enter`, not `{ENTER}`.
- **A running Deguffer holds `!Distribution\Deguffer.Core.dll` open**, so `dotnet build` fails with
  MSB3021 until you stop the process. Stop it before rebuilding, and stop it before
  `git worktree remove`, which otherwise fails partway naming a path.
- **Captures land in the workspace `.drive-net/`**, which is git-ignored. The Bash tool's working
  directory drifts between calls, so a capture can land under the primary checkout rather than your
  worktree. Pass an absolute path.

## Before you screenshot anything

A capture of the preview flow shows the real cache paths and the real profile name of whoever took
it. [No secrets or personal data](../../../CLAUDE.md#no-secrets-or-personal-data-mandatory) covers
a screenshot exactly as it covers a fixture. Never attach a raw capture to an issue, a pull request
or a commit. Crop it, or re-capture against synthetic data.

## Reporting the result

Say what you drove and what you saw. If the app crashed, say so and give the crash log. If you
could not drive part of the change, the picker being the usual reason, say which part and how you
covered it instead. "Verified" is a claim about observed behaviour, so do not make it about work you
did not do.
