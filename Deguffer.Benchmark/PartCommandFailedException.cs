namespace Deguffer.Benchmark;

/// <summary>The command run before each part of the race failed, so the parts after it would not be measured as asked.</summary>
internal sealed class PartCommandFailedException(string message) : Exception(message);
