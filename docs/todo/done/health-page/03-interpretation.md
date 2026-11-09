# Stage 03: interpretation

> **Status:** ✅ COMPLETE — 2026-11-09. Prerequisites: stage 02.

## Touch points

| File | Change |
| --- | --- |
| `Deguffer.Core/Health/HealthAttribute.cs` | New. The one row shape: key, name, reading, meaning, standing, raw text. |
| `Deguffer.Core/Health/AtaAttributeCatalogue.cs` | New. ID to name, explanation, raw format, role. |
| `Deguffer.Core/Health/AtaVendorOverlay.cs` | New. The entries H8 names, by model prefix. |
| `Deguffer.Core/Health/AtaAttributeRows.cs` | New. Decodes raw by format, and decides each ATA standing. |
| `Deguffer.Core/Health/NvmeAttributeRows.cs` | New. The health log as rows, and each NVMe standing. |
| `Deguffer.Core/Health/HealthText.cs` | New. Durations, counts, temperatures in words. |

## Design

`AttributeRole` is `FailureCount`, `RemainingLife`, `Temperature`, `Usage` or `Information`.
`SmartRawFormat` is `Count`, `Hours`, `Celsius` (low byte), `Percent`, `Bytes512` or `Opaque`.

Standing rules, each stated in the row's sentence:

- NVMe critical-warning bits other than temperature: Failing. The temperature bit: Watch.
- NVMe percentage used of 90 or more, media errors above 0, temperature at or above a non-zero
  `WCTEMP`: Watch.
- ATA failure prediction flag: Failing.
- ATA value at or below a threshold of 1 to 253, on a pre-fail attribute, with a value from 1 to
  253: Failing.
- ATA `FailureCount` raw above 0 with a known format: Watch.
- ATA `RemainingLife` value of 10 or less with a known format: Watch.

Meanings are written here in our own words from public vendor documentation. No third-party
attribute database is copied.

## Done when

- [x] Temperature takes the low byte of a packed raw value.
- [x] An unknown ID yields a "vendor-specific" row, and an `Opaque` raw value never makes a row Watch or Failing.
- [x] A vendor overlay wins only for its own model prefix.
- [x] Each standing rule has a test that fails without it.
- [x] `WCTEMP` of 0 and thresholds of 0, 254 and 255 cause no standing.
