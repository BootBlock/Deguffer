# Installed apps — build plan

> **Status:** 🟢 ACTIVE — the build plan for [_spec.md §7.3](_spec.md#73-installed-apps--the-entries-windows-lists).
> The spec decides what is built; this file records the order it is built in and what each stage
> has landed. Stages 1 to 5 are open.

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

Each stage lands on `main` with its tests before the next starts.

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
