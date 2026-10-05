namespace Deguffer.Benchmark;

/// <summary>
/// What one run of a route produced, before anything is timed around it.
/// </summary>
/// <param name="Items">Records read from the table, or entries the walk listed.</param>
/// <param name="BytesRead">Bytes read from the volume. Zero for the walk, which cannot see them.</param>
/// <param name="Complete">
/// Whether the route answered for everything. The table was read to its end and, for the index, the
/// build did not abandon the volume; or the walk was refused no folder. A run that gave up early is
/// faster for the wrong reason, so the report counts these rather than hiding them in the median.
/// </param>
internal readonly record struct RunTally(long Items, long BytesRead, bool Complete);
