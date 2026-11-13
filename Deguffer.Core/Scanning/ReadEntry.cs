using Deguffer.Core.Safety;

namespace Deguffer.Core.Scanning;

/// <summary>
/// One attribute read of a path a measurement was handed, as
/// <see cref="LongPath.ReadEntry(string, out EntryReading)"/> reads it: the seam a test counts the
/// reads through, and holds one read until another has started.
/// </summary>
internal delegate PathPresence ReadEntry(string path, out EntryReading reading);
