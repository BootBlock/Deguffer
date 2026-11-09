# Stage 04: sources, assessment and the reader

> **Status:** 🟢 ACTIVE — stage complete 2026-11-09, within the open plan in [00-overview.md](00-overview.md). Prerequisites: stage 03.

## Touch points

| File | Change |
| --- | --- |
| `Deguffer.Core/Health/DriveHealth.cs` | New. The report, `DriveKey`, `DriveFact`, `DriveVerdict`. |
| `Deguffer.Core/Health/IDriveHealthSource.cs` | New. |
| `Deguffer.Core/Health/NvmeHealthSource.cs` | New. |
| `Deguffer.Core/Health/AtaHealthSource.cs` | New. Also reads the elevated extras (H11). |
| `Deguffer.Core/Health/DriveAssessment.cs` | New. Worst standing, primary signal, Unknown. |
| `Deguffer.Core/Health/DriveFacts.cs` | New. The drive's details as labelled lines. |
| `Deguffer.Core/Health/DriveHealthReader.cs` | New. The two phases, the limit, and the in-flight join (H12). |
| `Deguffer.Core/Health/DriveSelection.cs` | New. Which row is selected after the list changes. |
| `Deguffer.Core/Health/DriveOverview.cs` | New. The page headline, which reassures only once every drive has answered. |
| `Deguffer.Core/Health/SingleRun.cs` | New. One run of device work at a time. |

## Design

```csharp
public sealed class DriveHealthReader
{
    public Task<IReadOnlyList<DriveDescription>?> ListAsync(CancellationToken ct);   // phase one
    public Task ReadAsync(IReadOnlyList<DriveDescription> drives, IProgress<DriveHealth> health, IProgress<DriveVolumes> volumes, CancellationToken ct);
}
```

Volumes per disk come from `VolumeMedia.PhysicalDisks` through a fresh `VolumeMediaCache` per read,
so a swapped disk is seen by Refresh. They are reported apart, because a volume can stall where a
drive does not. `SingleRun` holds each drive, the listing and the volumes to one blocked thread.

`MoreWhenElevated` is true where a `Command` question failed with `ERROR_ACCESS_DENIED`.

## Done when

- [x] Verdict: worst standing wins. Without the primary signal it is Unknown, and never Good.
- [x] A USB drive yields details and the "not reported" sentence, and no health question is asked.
- [x] An ATA drive that refuses the elevated questions still gives its attributes and sets
      `MoreWhenElevated`. With thresholds absent, no comparison is made.
- [x] A drive rejecting every question yields a row, and does not end the reading of the others.
- [x] A drive that does not answer in the limit reads "did not answer". The next read joins it and
      does not ask again.
- [x] Cancellation stops further disks being read.
