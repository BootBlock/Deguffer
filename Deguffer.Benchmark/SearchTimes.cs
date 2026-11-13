using Deguffer.Core.Duplicates;

namespace Deguffer.Benchmark;

/// <summary>What one run of the duplicates route took, stage by stage, and what it confirmed.</summary>
/// <param name="Stages">How long each part of the search took, what each read, and each lane of its content comparison.</param>
/// <param name="Groups">How many groups of copies the search confirmed.</param>
internal sealed record SearchTimes(DuplicateSearchTimings Stages, int Groups);
