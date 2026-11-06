namespace Deguffer.Core.Scanning.Mft;

/// <summary>One step of opening a volume's file table, how long it took, and how many reads it made.</summary>
/// <param name="Reads">How many reads of the volume the step asked for.</param>
/// <param name="BytesRead">The bytes those reads asked for.</param>
public readonly record struct MftOpenStepTime(MftOpenStep Step, TimeSpan Elapsed, int Reads, long BytesRead);
