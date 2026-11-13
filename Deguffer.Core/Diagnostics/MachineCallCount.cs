namespace Deguffer.Core.Diagnostics;

/// <summary>How many times one <see cref="MachineCall"/> was made, and the time spent in them all.</summary>
/// <param name="Calls">The calls that were made, whatever each answered.</param>
/// <param name="Elapsed">The time from the start of each call to its end, summed. Calls made side by side each count their own.</param>
public readonly record struct MachineCallCount(int Calls, TimeSpan Elapsed);
