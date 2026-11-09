# Stage 01: the device seam

> **Status:** 🟢 ACTIVE — in progress. Prerequisites: none.

## Touch points

| File | Change |
| --- | --- |
| `Deguffer.Core/Scanning/Media/DeviceCalls.cs` | New. Disk open, `OpenForCommands`, `Query`, `PropertyQuery`, moved from `StorageQueries`. |
| `Deguffer.Core/Scanning/Media/StorageQueries.cs` | Uses `DeviceCalls`. Behaviour unchanged. |
| `Deguffer.Core/Scanning/Media/StorageBusType.cs` | Gains `Scsi`, `Ata`, `Raid`, `Sas`, `Sata`, `Spaces`, used by H7. |
| `Deguffer.Core/Health/IDriveProbe.cs` | New. `IDriveProbe`, `IDriveDevice`. |
| `Deguffer.Core/Health/DriveQuestion.cs` | New enum of everything the page can ask. |
| `Deguffer.Core/Health/DriveRequests.cs` | New. The one table of commands (H3). |
| `Deguffer.Core/Health/DriveProbe.cs` | New. The real probe. |
| `Deguffer.Core/Health/DeviceInterfaces.cs` | New. `CM_Get_Device_Interface_List*`, retrying on `CR_BUFFER_SMALL`. |
| `Deguffer.Testing/FakeDriveProbe.cs` | New. Keyed by (disk, question). |

## Design

```csharp
public interface IDriveProbe
{
    IReadOnlyList<int> PresentDisks();
    IDriveDevice Open(int disk);
}

public interface IDriveDevice : IDisposable
{
    StorageAnswer Ask(DriveQuestion question);
}
```

`DriveRequest(uint ControlCode, byte[] Input, int OutputBytes, DriveAccess Access)` is one row.
`Access` is `Query` (no rights) or `Command` (`GENERIC_READ | GENERIC_WRITE`). The device opens each
handle at most once, and a refused open is a `StorageAnswer.Failed(5)`.

ATA inputs: SMART features use command `0xB0` and cylinder `0xC2 4F`. Thresholds is feature `0xD1`.
Read Log is feature `0xD5` with the log address in the sector number register. Identify is command
`0xEC`.

`\\.\PhysicalDriveN` is a device name and not a filesystem path, so §6.3's `LongPath` does not apply.

## Done when

- [ ] `DriveRequestsTests` iterates every `DriveQuestion` and decodes its code, command, feature,
      log address and NVMe page. Only `0xD1`, `0xD5` with logs `0x01` and `0x06`, and `0xEC` are
      allowed.
- [ ] `DriveHealthSourcesTests` sweeps the source: only `DriveProbe` calls `OpenForCommands`, and
      only `DriveProbe` and `StorageQueries` call `DeviceCalls.Query`.
- [ ] `DeviceInterfaces` multi-string parsing is tested on synthetic buffers.
- [ ] Existing `StorageQueries` callers and tests pass unchanged.
