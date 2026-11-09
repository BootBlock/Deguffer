# Stage 04: sources, assessment and the reader

> **Status:** 🟢 ACTIVE — not started. Prerequisites: stage 03.

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

## Design

```csharp
public sealed class DriveHealthReader
{
    public IReadOnlyList<DriveDescription> List();            // phase one
    public Task ReadAsync(IReadOnlyList<DriveDescription> drives, IProgress<DriveHealth> progress, CancellationToken ct);
}
```

Volumes per disk come from `VolumeMedia.PhysicalDisks` through a fresh `VolumeMediaCache` per read,
so a swapped disk is seen by Refresh.

`MoreWhenElevated` is true where a `Command` question failed with `ERROR_ACCESS_DENIED`.

## Done when

- [ ] Verdict: worst standing wins. Without the primary signal it is Unknown, and never Good.
- [ ] A USB drive yields details and the "not reported" sentence, and no health question is asked.
- [ ] An ATA drive that refuses the elevated questions still gives its attributes and sets
      `MoreWhenElevated`. With thresholds absent, no comparison is made.
- [ ] A drive rejecting every question yields a row, and does not end the reading of the others.
- [ ] A drive that does not answer in the limit reads "did not answer". The next read joins it and
      does not ask again.
- [ ] Cancellation stops further disks being read.
