namespace Deguffer.Core.Scanning.Mft;

/// <summary>
/// What the table says one guard's removal would take from a location. See
/// <see cref="MftMeasurement"/>.
/// </summary>
/// <param name="Size">The bytes and entries the removal takes.</param>
/// <param name="WithheldRecent">
/// Whether the guard left at least one real entry out of <paramref name="Size"/>. See
/// <see cref="ScanResult.WithheldRecent"/> for the claim that rests on telling an empty location from
/// a wholly recent one.
/// </param>
public readonly record struct MftReading(ScanSize Size, bool WithheldRecent);
