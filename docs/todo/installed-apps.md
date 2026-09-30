# Installed apps — build plan

> **Status:** 🟢 ACTIVE — the build plan for [_spec.md §7.3](_spec.md#73-installed-apps--the-entries-windows-lists).
> The spec decides what is built; this file records the order it is built in and what each stage
> has landed. Stages 1 to 4 are built; stage 5 is driven unelevated, and the elevated run is open.

The page lists the entries under the `Uninstall` keys, splits them into stale and installed by
evidence, removes stale entries (with a `reg.exe` backup by default), restores a backup, and runs an
installed program's own uninstaller. The reference is an earlier WinForms tool by the same author,
which read the same keys, had no evidence, no backup and no confirmation, and dropped an entry whose
values had an unexpected type. None of its code is carried over.

## Measured facts the design rests on

Taken on a Windows 11 26100 workstation, unelevated, on 2026-09-30. Re-measure before relying on
them against a very different build.

- `MsiQueryProductState` answers without elevation. It returned `INSTALLSTATE_DEFAULT` (5) for all
  520 entries that set `WindowsInstaller=1`, `INSTALLSTATE_UNKNOWN` (-1) for 45 entries whose key
  name is a product code but which do not set it, and `INSTALLSTATE_INVALIDARG` (-2) for a code
  without braces.
- The entries of this account's 12 per-user Windows Installer products were all under the
  machine-wide key, none under `HKEY_CURRENT_USER`.
- `reg.exe export` works unelevated for a machine-wide `Uninstall` entry, writes UTF-16LE with a
  byte-order mark, includes subkeys, and exits 1 for both a missing key and a refusal, with localised
  text. Without `/y` it waits on an overwrite prompt when the file exists, so it is always given `/y`.
- The machine-wide `Uninstall` key grants `Users` read access only. No entry on the machine denied
  `Administrators`, and none was owned by `TrustedInstaller`.
- Uninstall command shapes: 305 `MsiExec.exe /X{code}`, 214 `MsiExec.exe /I{code}`, 118 starting
  with a quoted path, 8 unquoted paths with spaces, 5 `RunDll32`, 4 `winget uninstall`; 523 values
  are `REG_EXPAND_SZ`.

## Stages

Each stage is committed on the feature branch with its tests before the next starts, and the
branch lands on `main` once stage 5 has passed.

1. **Reading and evidence (Core).** An `IUninstallRegistry` seam that reads the three sources by
   view, values typed as they are; the listing rules; an `IWindowsInstaller` seam over
   `MsiQueryProductState`; uninstall-command parsing; the stale-or-installed decision with its
   sentence. Tests through fakes, plus a scratch-key test of the real reader.
2. **Removal, backup and restore (Core).** The policy decided twice, `reg.exe` export and import
   through `IProcessRunner`, deletion of the one key, the §5.6 check, and the confirmation words.
3. **Uninstall (Core).** The command for each kind of entry, the refusals, a launcher seam that runs
   it through the shell and watches it with no deadline, and the after-the-fact report.
4. **The page (App).** The navigation item, the two lists updated in place, the hidden-entry switch,
   the backup switch as a preference, the confirmation dialogs, the elevated reopen, and the backup
   list.
5. **Verification.** Drive the page with the `verify` skill, unelevated and elevated.

## Status log

- 2026-09-30: §7.3 written, and §2 and §6.3 amended to match.
- 2026-09-30: stage 1 landed on the branch: `Deguffer.Core/InstalledApps` reads the keys and decides
  each entry's list.
- 2026-09-30: stage 2 landed on the branch: `EntryRemover`, `RegistryBackups` and `BackupRestorer`,
  with a real `reg.exe` export, delete and import round trip on a scratch key.
- 2026-09-30: stage 3 landed on the branch: `UninstallPolicy`, `ProgramUninstaller` and the
  `IUninstallLauncher` seam over the shell.
- 2026-09-30: stage 4 landed on the branch: the Installed apps page, its navigation item, the
  `--installed-apps` elevated reopen and the `BackUpInstalledAppEntries` preference.
- 2026-09-30: stage 5, driven unelevated against a build of the branch: the rail item
  opens the page, both lists fill, the filter narrows them, a scratch per-user stale entry was
  removed with a backup and restored from the Backups list, a machine-wide stale entry states that
  it needs administrator rights, and the backup switch persists. The run found that a program on a
  disconnected drive would read as stale, that the §5.6 summary counted one aggregate check, and
  that the restore note went stale after a restore; all three are fixed with tests. An elevated run
  is not yet done.

## Open before landing

A review of the branch found four ways an installed program can still read as stale, and a ruling
on Burn bundles is taken. Each is a change to `StaleRule` with tests that fail without it.

1. **An `InstallLocation` that is set but is not a full path**, or is not a string, reads as "names
   no install folder" and leads to stale. Keep "not set" apart from "set but not checkable"; the
   second is unproven.
2. **An `InstallLocation` that names a file** is probed as a directory and reads as absent. Probe it
   as either kind (`LongPath.ProbeEntry`).
3. **A link on the way to an unplugged drive** (`C:\Games` a junction to a removed `E:\`) reads as
   absent while the drive check passes on `C:\`. Walk the segments below the root, as
   `DerivedPath.FirstObstacleBetween` does, and treat a link or a refused segment as unproven.
4. **32-bit and 64-bit folder views.** The x86 build probes `System32` through redirection, and a
   32-bit entry's `%ProgramFiles%` expands to the 64-bit folder in the x64 build. Count an
   uninstaller or folder absent only when every view of it is absent: `System32`, `SysWOW64` and
   `Sysnative`, and `Program Files` and `Program Files (x86)`, from `ISystemDirectories`.

Items 2 to 4 want a path-probe seam (file, entry, directory with its link answer) so the rule is
tested without the real machine; the reader's per-reading cache implements it.

**Burn bundles (ruling: judge a bundle by the products it installed).** Measured on the same
workstation, from the WiX engine source (`src/burn/engine/registration.h`, `dependency.cpp`):

- A bundle's entry carries `BundleProviderKey`, `BundleCachePath`, `BundleUpgradeCode`,
  `EngineVersion` and others, but a leftover shell may carry only `Resume` and `Installed`.
- Packages register under `Software\Classes\Installer\Dependencies\<providerKey>` in HKLM for a
  per-machine package and HKCU for a per-user one; the key is shared across registry views and
  readable unelevated. A package provider lists the bundle under `Dependents\<bundle id>`, where the
  bundle id is the `Uninstall` key name, never `BundleProviderKey`. The provider's default value is
  the package's ProductCode (MSI), a PatchCode (MSP), or nothing (EXE, MSU, and WiX 3.6 and 3.8).
- A bundle is stale only when at least one package provider lists it, every such provider's default
  value is a product code, and Windows Installer knows none of them. A provider with no default
  value, a non-GUID value, a patch code (recognise it rather than read its `-1`), or no provider at
  all leaves the bundle unproven. The bundle's own provider is excluded, and codes are deduplicated.
