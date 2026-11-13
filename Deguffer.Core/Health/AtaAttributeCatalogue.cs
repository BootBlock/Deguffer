using System.Collections.Frozen;

namespace Deguffer.Core.Health;

/// <summary>What an ATA attribute's value says, which decides how the page states and judges it.</summary>
internal enum AttributeRole
{
    /// <summary>A count of faults, where anything above zero is a concern.</summary>
    FailureCount,

    /// <summary>A normalised value that counts down the drive's remaining life.</summary>
    RemainingLife,

    Temperature,

    /// <summary>A measure of how much the drive has been used.</summary>
    Usage,

    /// <summary>Reported for reference, with no judgement made.</summary>
    Information,
}

/// <summary>How an attribute's six raw bytes are read.</summary>
internal enum SmartRawFormat
{
    /// <summary>A count, as wide as the definition's <see cref="RawWidth"/>.</summary>
    Count,

    /// <summary>Hours, as wide as the definition's <see cref="RawWidth"/>.</summary>
    Hours,

    /// <summary>Degrees in the low byte. Many drives pack the lowest and highest seen above it, each differently.</summary>
    Celsius,

    /// <summary>Degrees in bytes 0-1, the lowest seen in bytes 2-3 and the highest seen in bytes 4-5.</summary>
    CelsiusRange,

    /// <summary>The raw value is not used. The normalised value is a percentage.</summary>
    Percent,

    /// <summary>A count of 512-byte sectors.</summary>
    Bytes512,

    /// <summary>A count of the drive's logical sectors, whose size Windows reports.</summary>
    LogicalSectors,

    /// <summary>A count of 32 MiB units, each 65,536 sectors of 512 bytes, as wide as the definition's <see cref="RawWidth"/>.</summary>
    Units32MiB,

    /// <summary>Minutes, as wide as the definition's <see cref="RawWidth"/>. FFFFh means the drive has not measured it yet.</summary>
    Minutes,

    /// <summary>The raw value divided by 1024 is a percentage. FFFFh means the drive has not measured it yet.</summary>
    PercentPer1024,

    /// <summary>The raw value is a whole percentage. FFFFh means the drive has not measured it yet.</summary>
    RawPercent,

    /// <summary>
    /// Degrees in bytes 0-1, the lowest seen in byte 2, the highest seen in byte 3, and in bytes 4-5 the
    /// times the drive ran hotter than it is rated for.
    /// </summary>
    CelsiusWithOverheats,

    /// <summary>
    /// A power-loss capacitor test: the microseconds the capacitor took to discharge in bytes 0-1, the
    /// minutes since that test in bytes 2-3, and the number of tests in bytes 4-5.
    /// </summary>
    CapacitorTest,

    /// <summary>How hard the drive is slowing itself to cool down, as a percentage in byte 0, and the times it has done so in bytes 1-4.</summary>
    ThrottleStatus,

    /// <summary>Manufacturers use these bytes differently, so they are shown as they are and judged by nothing.</summary>
    Opaque,

    /// <summary>The raw value is one of the codes a manufacturer's document lists, each with its own meaning.</summary>
    Status,
}

/// <summary>How much of the six raw bytes a count or a number of hours fills.</summary>
internal enum RawWidth
{
    /// <summary>The low 32 bits, because many drives pack other figures above them.</summary>
    LowFourBytes,

    /// <summary>All six bytes, where the manufacturer's document says the figure fills them.</summary>
    AllSixBytes,
}

/// <summary>What the page knows about one attribute.</summary>
internal record AtaAttributeDefinition(
    string Name,
    string Explanation,
    SmartRawFormat Format,
    AttributeRole Role,
    RawWidth Width = RawWidth.LowFourBytes);

/// <summary>What one documented status code means, and whether it is a concern.</summary>
internal sealed record AtaStatusCode(string Reading, string Meaning, AttributeStanding Standing);

/// <summary>
/// An attribute whose raw value is a status code. A code the document does not list is shown as
/// reported and judged by nothing, because guessing a meaning would mislead.
/// </summary>
internal sealed record AtaStatusDefinition(string Name, string Explanation, FrozenDictionary<ulong, AtaStatusCode> Codes)
    : AtaAttributeDefinition(Name, Explanation, SmartRawFormat.Status, AttributeRole.Information);

/// <summary>
/// The common ATA SMART attributes, written from manufacturers' public documentation. An ID absent
/// here is shown as vendor-specific with its numbers, because guessing a meaning would mislead.
/// </summary>
internal static class AtaAttributeCatalogue
{
    private static readonly FrozenDictionary<byte, AtaAttributeDefinition> Known = new Dictionary<byte, AtaAttributeDefinition>
    {
        [1] = Reference("Read error rate", "How often reads needed correcting, on the manufacturer's own scale."),
        [2] = Reference("Throughput performance", "The drive's overall transfer efficiency, on the manufacturer's own scale."),
        [3] = Reference("Spin-up time", "How long the platters take to reach full speed."),
        [4] = Used("Start and stop count", "How many times the drive has started and stopped spinning.", SmartRawFormat.Count),
        [5] = Fault("Reallocated sectors", "Damaged areas the drive has replaced with spares."),
        [7] = Reference("Seek error rate", "How often the heads missed their position, on the manufacturer's own scale."),
        [8] = Reference("Seek time performance", "The average speed of head movements, on the manufacturer's own scale."),
        [9] = Used("Time powered on", "How long the drive has been switched on over its life.", SmartRawFormat.Hours),
        [10] = Fault("Spin-up retries", "Times the drive failed to reach full speed at the first attempt."),
        [11] = Plain("Recalibration retries", "Times the drive had to repeat finding its starting position."),
        [12] = Used("Power cycles", "How many times the drive has been switched on.", SmartRawFormat.Count),
        [13] = Reference("Soft read errors", "Read errors corrected without a retry, on the manufacturer's own scale."),
        [170] = Reference("Reserve block usage", "How much of the reserve space is in use."),
        [171] = Reference("Program failures", "Failed writes to flash memory."),
        [172] = Reference("Erase failures", "Failed erases of flash memory."),
        [173] = Reference("Wear levelling", "How evenly flash memory has been used."),
        [174] = Used("Unexpected power loss", "Times power was lost without the drive being told first.", SmartRawFormat.Count),
        [177] = Reference("Wear levelling count", "How much flash memory has been worn."),
        [184] = Fault("End-to-end errors", "Data found corrupted between the drive's interface and its storage."),
        [187] = Fault("Reported uncorrectable errors", "Reads the drive could not correct and reported as failed."),
        [188] = Reference("Command timeouts", "Commands that took too long."),
        [189] = Reference("High-fly writes", "Writes made with the head flying too high."),
        [190] = Warmth("Airflow temperature", "The temperature of the air passing over the drive."),
        [191] = Used("G-sense errors", "Shocks or vibration the drive has detected.", SmartRawFormat.Count),
        [192] = Used("Emergency head retracts", "Times the heads were parked because power was lost.", SmartRawFormat.Count),
        [193] = Used("Load cycles", "How many times the heads have parked and returned.", SmartRawFormat.Count),
        [194] = Warmth("Temperature", "The temperature the drive measures inside itself."),
        [195] = Reference("Hardware error correction", "Errors corrected by the drive's own circuits, on the manufacturer's own scale."),
        [196] = Fault("Reallocation events", "Times the drive has moved data away from a damaged area."),
        [197] = Fault("Pending sectors", "Unstable areas waiting to be tested and replaced."),
        [198] = Fault("Uncorrectable sectors", "Areas the drive could not read during its own background scan."),
        [199] = Plain("Interface errors", "Corrupted transfers on the cable. Usually a loose or faulty cable, not the drive."),
        [200] = Reference("Write error rate", "How often writes needed correcting, on the manufacturer's own scale."),
        [201] = Reference("Soft read error rate", "Read errors that were corrected, on the manufacturer's own scale."),
        [202] = Reference("Vendor life measure", ""),
        [220] = Reference("Disk shift", "How far the platters have moved from their original position."),
        [222] = Used("Time with heads loaded", "How long the heads have been over the platters.", SmartRawFormat.Hours),
        [223] = Plain("Load retries", "Times the heads had to retry moving over the platters."),
        [224] = Reference("Load friction", "Resistance while the heads move, on the manufacturer's own scale."),
        [226] = Reference("Load time", "How long the heads take to load."),
        [230] = Reference("Vendor drive measure", ""),
        [231] = Reference("Vendor life or temperature measure", ""),
        [232] = Reference("Endurance remaining", ""),
        [233] = Reference("Vendor wear or hours measure", ""),
        [240] = Used("Head flying time", "How long the heads have been positioned over the platters.", SmartRawFormat.Hours),
        [241] = Reference("Total data written", "How much has been written."),
        [242] = Reference("Total data read", "How much has been read."),
    }.ToFrozenDictionary();

    internal static AtaAttributeDefinition? Of(byte id) => Known.GetValueOrDefault(id);

    private static AtaAttributeDefinition Fault(string name, string explanation) =>
        new(name, explanation, SmartRawFormat.Count, AttributeRole.FailureCount);

    private static AtaAttributeDefinition Used(string name, string explanation, SmartRawFormat format) =>
        new(name, explanation, format, AttributeRole.Usage);

    private static AtaAttributeDefinition Warmth(string name, string explanation) =>
        new(name, explanation, SmartRawFormat.Celsius, AttributeRole.Temperature);

    private static AtaAttributeDefinition Plain(string name, string explanation) =>
        new(name, explanation, SmartRawFormat.Count, AttributeRole.Information);

    private static AtaAttributeDefinition Reference(string name, string explanation) =>
        new(name, explanation, SmartRawFormat.Opaque, AttributeRole.Information);
}
