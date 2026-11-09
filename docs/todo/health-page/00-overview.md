# Health page: plan overview

> **Status:** 🟢 ACTIVE — shipped 2026-11-09, with one check outstanding: a driven run as administrator
> on a SATA drive (stage 08), which needs a UAC prompt accepted. Moves to
> `docs/todo/done/`. Spec section: [_spec.md](../_spec.md) §7.6, added by stage 07.

A new rail destination, **Health**, lists every physical drive with what Windows can report about
its condition: an interpreted SMART reading in plain words, the raw values behind it, and the
drive's other details. It reads and explains. It changes nothing on any drive.

## What was measured

A read-only probe on one machine (one SATA SSD, two NVMe SSDs), unelevated and with a handle opened
with no access rights:

| Query | SATA | NVMe |
| --- | --- | --- |
| `IOCTL_STORAGE_QUERY_PROPERTY`: device, adapter, seek penalty, TRIM, alignment, I/O capability | answers | answers |
| `StorageDeviceTemperatureProperty` (52) | error 1 | answers |
| `StorageDeviceProtocolSpecificProperty` (50): NVMe Identify Controller, health log (page 2), self-test log (page 6) | error 1 | answers |
| `IOCTL_STORAGE_PREDICT_FAILURE` | answers: flag and full 512-byte attribute table | error 1 |
| `SMART_RCV_DRIVE_DATA` (thresholds, IDENTIFY, logs) | error 5 unelevated | not applicable |
| `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX` | answers | answers |

Opening `GENERIC_READ | GENERIC_WRITE` unelevated fails with error 5. So the attribute table, the
overall failure prediction and all NVMe data need no elevation. Elevation adds only the SATA detail
behind `SMART_RCV_DRIVE_DATA`. The elevated answers were not measured: they follow the published
ATA command set.

## Locked decisions

- **H1. Lazy and kept.** The page is a `Frame` destination, so nothing exists until the first
  visit. No `App` static, timer, or service touches a drive before it. `HealthPage` sets
  `NavigationCacheMode.Required`, so the page, its rows and its selection survive every switch.
- **H2. Read on first arrival, then on Refresh only.** `HealthViewModel.EnsureRead` reads once on
  the first `Loaded`. No polling: a read can wake a sleeping disk. Leaving the page does not cancel
  a read. A read is never a `RunningActions` action, so it never holds the window open.
- **H3. Read-only commands, by construction.** Every command is one row of `DriveRequests`. A test
  decodes every row: control code, ATA command, feature, log address, NVMe page. A source sweep
  holds `DeviceCalls.OpenForCommands` to `DriveProbe`. No self-test start, no SMART enable or
  disable, no vendor command.
- **H4. Drives are enumerated, never guessed.** `CM_Get_Device_Interface_List` for
  `GUID_DEVINTERFACE_DISK`, then `IOCTL_STORAGE_GET_DEVICE_NUMBER` through the table.
- **H5. Raw bytes at the seam.** `IDriveProbe` and `IDriveDevice` return `StorageAnswer`. Pure
  parsers read the bytes, so a test states any drive the machine does not have.
- **H6. One disk-query path.** The disk open, query and property-query layout move from
  `StorageQueries` to `DeviceCalls`, and both it and `DriveProbe` use them. Other `CreateFile`
  callers open files and are not in scope.
- **H7. A source per protocol.** `IDriveHealthSource` has `NvmeHealthSource` and `AtaHealthSource`.
  `DriveHealthReader` picks by bus and holds no protocol knowledge. `StorageBusType` gains the
  members this needs. Any other bus gets device details and one sentence saying Windows does not
  report its health that way.
- **H8. Interpretation is data.** ATA attributes are a `FrozenDictionary` catalogue. Unknown IDs
  show as vendor-specific with their numbers. An ID whose meaning differs by vendor is `Opaque`
  unless `AtaVendorOverlay` names it: Samsung 177, 241, 242. Crucial and Micron 202. Intel 233.
  An `Opaque` raw value never affects the verdict. A value at or below its threshold still does, because that comparison is the drive's own.
- **H9. One judgement per reading.** Each protocol's row builder gives each attribute one
  `AttributeStanding`, and the row's sentence states why. `DriveAssessment` holds no protocol
  knowledge: the verdict is the worst standing. **Good needs the drive's primary signal** (NVMe
  critical warning, or ATA failure prediction). Without it the verdict is Unknown. Good reads "No
  problems reported", and the page always says SMART cannot predict every failure.
- **H10. Elevation is an offer, with a banner.** `ElevationOffer.ShouldOffer(isElevated, reports)`
  is true where a command question was refused with access denied. One view-model property drives
  an `InfoBar` (`IsClosable="False"`) and the header's "Reopen as administrator" button. A machine
  where elevation would add nothing shows neither. `ElevationRequest.Health` reopens on this page.
- **H11. Elevated extras are SATA thresholds, IDENTIFY and logs.** Thresholds add a comparison per
  attribute. IDENTIFY adds rotation and link speed. Log `0x01` adds the device error count, and log
  `0x06` the latest self-test.
- **H12. Two-phase, bounded reading.** Phase one enumerates and reads each device descriptor,
  which needs no health query and has the same 30-second limit, and shows every drive at once, ordered by
  disk number. Phase two reads each drive in parallel (`MaxDegreeOfParallelism` 4) and fills its
  row. A drive gets 30 seconds, then reads "did not answer". A device call cannot be interrupted,
  so a drive still being asked is joined by the next Refresh and never asked twice.
- **H13. Rows update in place.** Phase one calls `LiveList.Show` for the drives: that is the one
  place a drive leaves the list, and a failed or cancelled read never reaches it. Each report then
  writes over its own row. Facts and attributes are records updated by `LiveList`'s value overload.
- **H14. List and detail layout.** Drives on the left with a verdict chip and temperature. The
  selected drive fills the right, with its attribute `ListView` in a `*` row so it stays virtual.
  The lowest disk is selected as soon as phase one shows it. If the selected drive goes, the
  nearest row is selected.
- **H15. Serial numbers stay on screen.** `DriveKey` is an opaque hash. No record's `ToString`
  prints a serial, and fixtures use invented serials.
- **H16. Non-goals.** No history, no alerts, no USB SAT pass-through, no SMART control.

## Conceptual grounding

- An ATA attribute has a normalised **value** (higher is better, vendor-scaled), a **worst** value,
  a 6-byte **raw** counter and, elevated, a **threshold**. A value at or below a valid threshold
  (1 to 253) on a pre-fail attribute means the drive considers it failed.
- NVMe has no attribute table. The health log is one fixed structure.
- Windows has no route to USB bridge SMART without pass-through, so H16 excludes it.

## Stage map

| № | File | Gist | Status |
| --- | --- | --- | --- |
| 01 | [01-device-seam.md](01-device-seam.md) | `DeviceCalls`, `IDriveProbe`, `DriveRequests`, enumeration, the fake | complete |
| 02 | [02-parsers.md](02-parsers.md) | Pure readers for descriptors, NVMe and ATA structures | complete |
| 03 | [03-interpretation.md](03-interpretation.md) | ATA catalogue, NVMe rows, plain-language values | complete |
| 04 | [04-reader.md](04-reader.md) | Sources, assessment, `DriveHealthReader` | complete |
| 05 | [05-elevation.md](05-elevation.md) | `ElevationRequest.Health`, the offer rule | complete |
| 06 | [06-view-model.md](06-view-model.md) | Rows and `HealthViewModel` | complete |
| 07 | [07-page-and-docs.md](07-page-and-docs.md) | Page, rail, spec §7.6, README | complete |
| 08 | [08-verification.md](08-verification.md) | Full build, tests, driven runs, filing this plan | open: the elevated run |

The stages run in order. `MainWindow.xaml(.cs)` and `ElevationRequest.cs` are also edited by any
other new destination.

## Reference

- `Deguffer.App/MainWindow.xaml(.cs)`: rail, `OnDestinationChanged`, `OpenWhereTheLaunchAsked`.
- `Deguffer.App/Views/StartupPage.xaml(.cs)`, `ViewModels/StartupViewModel.cs`: page pattern.
- `Deguffer.Core/Scanning/Media/StorageQueries.cs`, `StorageDescriptors.cs`, `VolumeMediaCache.cs`.
- `Deguffer.Core/Execution/ElevationRequest.cs`, `ElevationOffer.cs`.
- `Deguffer.Core/Viewing/LiveList.cs`. `Deguffer.Core/Safety/IVolumeInventory.cs`.
