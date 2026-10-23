# What starts at sign-in — the investigation

> **Status:** 📘 REFERENCE — the investigation behind [_spec.md](_spec.md) §7.5, with what was
> measured and what was not. It records what one machine answered on one date, and stays as it was
> written. §7.5 states what Deguffer does about it.

The Memory view (§7.2) shows what is in memory now. It does not show what put it there at sign-in,
so a program closed from Memory comes back after the next restart if Windows starts it then.
[#360](https://github.com/BootBlock/Deguffer/issues/360) asked for the list of what starts at
sign-in, a link to it from Memory, and an offer to stop an entry starting the way Task Manager does,
without removing the entry. It asked first whether a documented route to do that exists for each kind
of entry, and said that where none exists, the entry is report-only.

## 1. Where Windows reads startup entries

Task Manager's Startup apps page and the Startup page in Settings list the same entries. Microsoft
states that "Task Manager provides the same list of startup applications offered by Settings"
([Microsoft Support](https://support.microsoft.com/en-us/help/5022092)). Neither documents the list's
sources. Observed on the measured machine, and consistent with third-party tools that reproduce the
list, they are:

| Source | Where |
| --- | --- |
| This user's `Run` key | `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` |
| The machine's `Run` key | `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`, 64-bit view |
| The machine's 32-bit `Run` key | The same path in the 32-bit view, which is `WOW6432Node` on disk |
| This user's Startup folder | The `Startup` known folder |
| Every user's Startup folder | The `Common Startup` known folder |
| Packaged applications' startup tasks | The `windows.startupTask` extension in each package's manifest |

Task Manager does not list `RunOnce`, the policy `Run` keys under `Policies\Explorer`, Winlogon's
values, scheduled tasks that run at log-on, or services. The first three were not measured here: a
third-party reproduction of the list reports that `RunOnce` is left out, and the other two are a
policy's and the sign-in process's own, not entries a user switches. Scheduled tasks and services
are out of scope whatever Task Manager does, because §2 rules out service control.

## 2. Whether a documented route exists to stop an entry starting

**For a `Run` value or a Startup folder item: no.** Windows documents no API, COM interface or
command that disables an entry and keeps it. `Win32_StartupCommand` lists entries, has no methods,
and says to change them by editing the registry, which means removing them
([Win32_StartupCommand](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-startupcommand)).
`ms-settings:startupapps` is documented, but it only opens the Settings page
([Launch the Settings app](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app)).

What Task Manager and Settings do instead is write a value under
`Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved`, in the hive of the entry, and
leave the entry alone. The subkey is `Run` for a `Run` value in the 64-bit view, `Run32` for one in
the 32-bit view, and `StartupFolder` for a Startup folder item. The value's name is the `Run` value's
name or the item's file name. None of this is documented: the description comes from third-party
analysis ([windowsir](http://windowsir.blogspot.com/2022/07/startupapprovedrun-pt-ii.html),
[Hexacorn](https://www.hexacorn.com/blog/?p=5992)) and from the measurement below.

**For a packaged application's startup task: only from inside the package.** Windows documents
`StartupTask`, and every member that reads or changes a task works on the calling package's own tasks
([StartupTask](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask)). The
same page says that once a task is enabled "the user is in control" through Settings or Task Manager.
Windows keeps the state for this user under
`HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\<package family>\<task>`,
as a `State` value whose numbers are `StartupTaskState`'s documented ones
([StartupTaskState](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptaskstate)).
The location is not documented, and the same key holds `LastDisabledTime` and
`UserEnabledStartupOnce`, whose meaning to the application model is not documented either.

## 3. What was measured

One workstation, Windows 11 24H2 (build 26100), on 2026-10-23, unelevated except where stated. No
name, path or program from the machine is recorded here.

**Every `StartupApproved` value was 12 bytes of `REG_BINARY`**: a four-byte state, little-endian, and
an eight-byte FILETIME.

| Key | Values | State 2, zero time | State 3, with a time | State 1, zero time | State 6, zero time |
| --- | --- | --- | --- | --- | --- |
| `HKCU\…\StartupApproved\Run` | 14 | 9 | 4 | 1 | 0 |
| `HKCU\…\StartupApproved\StartupFolder` | 4 | 3 | 1 | 0 | 0 |
| `HKLM\…\StartupApproved\Run` | 7 | 6 | 0 | 0 | 1 |
| `HKLM\…\StartupApproved\Run32` | 6 | 4 | 2 | 0 | 0 |
| `HKLM\…\StartupApproved\StartupFolder` | 3 | 1 | 2 | 0 | 0 |

`HKCU\…\StartupApproved\Run32` did not exist. Ten values named a `Run` value that no longer
existed, and Task Manager listed none of them. Two `Run` values had no `StartupApproved` value, and
Task Manager listed both as enabled. It listed the one entry with state 6 as enabled.

**What Settings and Task Manager write, measured on a `Run` value added for the purpose and removed
afterwards:**

| Step | Value afterwards |
| --- | --- |
| Before any change | No value |
| Settings, switched off | `01 00 00 00` and the current time |
| Settings, switched on | `00 00 00 00` and a zero time |
| Settings, switched off again | `01 00 00 00` and the current time |
| Task Manager opened, elevated | `03 00 00 00` and the time Settings wrote |
| Task Manager, Enable | `02 00 00 00` and a zero time |
| Task Manager, Disable | `03 00 00 00` and the current time |
| Task Manager, Enable | `02 00 00 00` and a zero time |

So the low bit of the state is "disabled", which is the rule third-party tools use, and Task Manager
sets the `0x02` bit on values it reads, keeping the time. Settings names the entry by the program's
file description, and Task Manager named it by the program's file name. Bit `0x04`, seen once,
is not explained by anything measured or found.

**Packaged startup tasks.** Nine tasks had a `State` value: five 0, one 1 and three 2. Task Manager
listed eight of them, with their packages' publishers, and showed 2 as enabled and 0 and 1 as
disabled. It did not list one task whose state was 2. Why was not established.

## 4. What follows

- **A `Run` value and a Startup folder item may be switched the way Task Manager does**, because the
  value Deguffer would write is the one Task Manager was measured writing, and the entry itself is
  untouched. The format is undocumented, so Deguffer replaces only a value whose every bit it can
  account for: no value, or the state 0, 1, 2 or 3 in 12 bytes of `REG_BINARY`. Any other value is
  Windows' or another program's record in a form nobody explained, and replacing it would discard
  what it says. Such an entry is report-only.
- **A packaged startup task is report-only.** Its documented control belongs to the application and
  the user, and the store is an application-model record with values beside `State` that nothing
  documents. Deguffer offers Windows' own Startup page in Settings, which is the documented route.
- **Re-enabling writes Task Manager's own enabled value**, `02 00 00 00` and a zero time, so the
  entry reads afterwards as Task Manager would have left it.

## 5. Limits that stay open

- Nothing here was measured on any other Windows build. A build that changed the format would show
  as values Deguffer does not recognise. A value of another length or type is shown as unknown, and
  a state with a bit nobody explained is shown by its low bit, as Task Manager showed one, and never
  replaced. Either way the entry is report-only.
- The publisher Task Manager shows was blank for every non-packaged entry on the measured machine,
  so where its publisher comes from was not established. Deguffer reads the program's own version
  information and says so.
- What Task Manager does with the `0x04` bit was not measured.
