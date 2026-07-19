# Native AOT and single-file publish — evaluation

> **Status:** 📘 REFERENCE — the evaluation is finished and both options are rejected. This
> records *why*, with the numbers, so the question can be reopened on evidence rather than
> re-litigated from scratch. Re-run the measurements before trusting them against a much newer
> Windows App SDK.

Measured against Windows App SDK 1.8.251216001, .NET SDK 10.0.302, ILCompiler 10.0.10,
Release x64, published to a clean directory (`.pdb` excluded).

## What was rejected, and why

| Configuration | Files | Size | Starts? |
| --- | ---: | ---: | --- |
| Current (self-contained, untrimmed) | 518 | 258.7 MB | yes |
| `PublishSingleFile=true` | 1 | 273.4 MB | yes |
| `PublishAot=true` | 286 | 109.5 MB | **no** |

### Single-file — rejected: it moves the file count rather than removing it

It builds and runs, but the bundled exe **self-extracts 508 files / 263.6 MB into
`%TEMP%\.net\Deguffer.App` on launch**. Peak disk cost becomes the 273 MB exe *plus* the 264 MB
extraction — worse than the 259 MB it replaces, and the extra copy lands in exactly the temp
directory a disk-cleanup tool exists to reclaim. The §6.3 premise (a machine too full to install a
runtime) argues against this, not for it.

It also requires `EnableMsixTooling=true`, which the Windows App SDK demands for embedded
`resources.pri` generation. That in turn duplicates what `PublishGeneratedXamlArtefacts` publishes
by hand, so adopting it would mean reworking that target too.

### Native AOT — rejected: trimming kills the XAML runtime at startup

Support is no longer the blocker. WinUI 3 has supported AOT since Windows App SDK 1.6, and the
compilation itself **succeeds** — the payload drops to 286 files / 109.5 MB, a 58% reduction, which
is a real prize.

The build is nonetheless unusable. AOT implies trimming (`PublishTrimmed=false` is rejected
outright by ILCompiler), and the published app exits **0xC000027B — `STATUS_STOWED_EXCEPTION`**
about 0.8s after launch, with no window and no entry in `crash.log`: the process dies before the
managed fault handlers are installed. The Windows Application event log puts the fault in
`Microsoft.UI.Xaml.dll`, not in application code.

This is the hazardous shape the project is built to avoid — a green build that produces a silently
broken executable. Note it is *not* the same defect as the missing generated XAML artefacts fixed
in 71ef12e: the single-file configuration exercises the same `EnableMsixTooling` publish path
without trimming and starts correctly, which isolates trimming as the cause.

**What was tried and did not help:** rooting the application assembly
(`TrimmerRootAssembly=Deguffer.App`), `TrimMode=copyused`, and preserving reflection metadata
(`IlcTrimMetadata=false`) — the last of which costs 5 MB of the saving and still exits 0xC000027B.
The types being trimmed away are inside the Windows App SDK's own XAML machinery, so rooting the
app's assemblies cannot reach them.

## If this is reopened

- Application code has its own AOT work outstanding, independent of the above: `PreferenceStore`
  and `ScanEstimateCache` use reflection-based `System.Text.Json` and raise IL2026/IL3050. Both
  want a `JsonSerializerContext` source-generator. That is worth doing on its own merits and is a
  prerequisite, not a fix for the startup crash.
- The remaining unknown is which Windows App SDK types the trimmer removes. Answering it needs a
  trim-analysis pass over the SDK assemblies and the trimmer roots to match, which is a larger
  piece of work than this evaluation.
- Per **G8**, nothing here is established by a green build. Any future attempt must launch the
  published exe and observe a window before claiming AOT works.
