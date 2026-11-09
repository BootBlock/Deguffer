# Stage 02: the parsers

> **Status:** 🟢 ACTIVE — not started. Prerequisites: stage 01.

## Touch points

| File | Change |
| --- | --- |
| `Deguffer.Core/Health/Parsing/DeviceDescriptor.cs` | New. `STORAGE_DEVICE_DESCRIPTOR`: bus, removable, vendor, product, revision, serial. |
| `Deguffer.Core/Health/Parsing/DriveProperties.cs` | New. TRIM, alignment, geometry, temperature. Seek penalty reuses `StorageDescriptors`. |
| `Deguffer.Core/Health/Parsing/ProtocolData.cs` | New. Finds the payload from the declared offset and length. |
| `Deguffer.Core/Health/Parsing/NvmeHealthLog.cs` | New. Log page 2. |
| `Deguffer.Core/Health/Parsing/NvmeController.cs` | New. Identify Controller: version, `WCTEMP`, `CCTEMP`. |
| `Deguffer.Core/Health/Parsing/AtaCommandAnswer.cs` | New. The 16-byte header, refusing a set driver error (`bIDEError` is valid only with one). |
| `Deguffer.Core/Health/Parsing/AtaAttributeTable.cs` | New. Attributes from `PREDICT_FAILURE`, thresholds merged by ID. |
| `Deguffer.Core/Health/Parsing/AtaIdentify.cs` | New. Rotation, link speed, SATA generation. |
| `Deguffer.Core/Health/Parsing/SelfTestLogs.cs` | New. One `SelfTestOutcome`, a reader per protocol. |
| `Deguffer.Core/Health/Parsing/AtaErrorLog.cs` | New. Device error count from log `0x01`, bytes 452 and 453. |

## Design

Each reader follows `StorageDescriptors`: `TryRead(ReadOnlySpan<byte>, out T)` checks the length
against the fields it reads and answers false rather than read past. NVMe 128-bit counters read as
`UInt128`. Protocol data is at 8 + `ProtocolDataOffset`, validated with `ProtocolDataLength`.

An ATA table starts 2 bytes in: `ID`, 2 flag bytes, value, worst, 6 raw bytes, 1 reserved, 12 bytes
each, 30 entries, ID 0 unused. A repeated ID keeps the first.

The ATA self-test log is circular: 24-byte entries from byte 2, with the 1-based index of the
latest at byte 508 and 0 meaning empty. Status is the high nibble of entry byte 1. The NVMe log is
newest first: 28-byte entries from byte 4, result in the low nibble of entry byte 0, `0xF` unused.

## Done when

- [ ] Each parser has a test on synthetic bytes for a good answer and for a truncated one.
- [ ] A descriptor offset of 0 or beyond the buffer reads as absent, and padding is trimmed.
- [ ] An ATA answer with its driver error set reads as no answer.
- [ ] A protocol offset or length outside the buffer reads as no answer.
- [ ] An all-zero table gives no attributes. A repeated ID gives one attribute.
