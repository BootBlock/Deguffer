using Deguffer.Core.Execution;
using Deguffer.Core.Scanning.Mft;

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
/// <param name="Reads">How many reads of records were asked for. Zero for the walk, which reads none.</param>
/// <param name="ClusterReads">
/// How many reads of clusters were asked for: the table's bitmap and the attribute lists kept outside
/// it. Zero for the walk, which reads none.
/// </param>
/// <param name="ListReadTime">
/// How long the reads of the attribute lists kept outside the table took, from the first starting to
/// the last ending. Zero for the walk, and for a table with no such list.
/// </param>
/// <param name="PeakListings">
/// The most folders the walk was listing at once. Zero for a table route, which lists no folders.
/// </param>
/// <param name="PeakWorkers">
/// The most threads the removal's worker pool held at once. Zero for a route that removes nothing.
/// </param>
/// <param name="Passes">
/// Where a removal route's removals spent their time, pass by pass, summed over every removal the run
/// made. Null for a route that removes nothing.
/// </param>
/// <param name="Steps">What the clean route's steps spent their time on. Null for every other route.</param>
/// <param name="MeanReadBytes">
/// The bytes of records over the reads of records. Zero for the walk, which reads none.
/// </param>
/// <param name="OpenTime">
/// How long opening the volume took, before the first stage: the boot sector and <c>$MFT</c>'s own
/// extent map. Zero for a route that does not read the table.
/// </param>
/// <param name="Open">
/// How long each step of opening the volume took, and how many reads each made. Null for a route
/// that does not read the table, and without steps where the volume was opened by something that
/// does not time them, as a test's fixture table is.
/// </param>
/// <param name="Stages">
/// How long each stage of reading the table and building from it took, and the counts that size
/// them. Null for a route that does not read the table.
/// </param>
/// <param name="Kept">
/// What the route built and a scan would keep, held only until <see cref="Timing"/> has measured
/// it (<see cref="RunSample.KeptBytes"/>), or null for a route that keeps nothing.
/// </param>
/// <param name="Race">What each part of a round of the race route took. Null for every other route.</param>
/// <param name="Planning">When each provider of the plan route's pass ran. Null for every other route.</param>
internal readonly record struct RunTally(
    long Items,
    long BytesRead,
    bool Complete,
    int PeakListings = 0,
    KeptResult? Kept = null,
    long Reads = 0,
    long ClusterReads = 0,
    TimeSpan ListReadTime = default,
    int PeakWorkers = 0,
    RemovalPasses? Passes = null,
    StepTimes? Steps = null,
    double MeanReadBytes = 0,
    MftStageTimings? Stages = null,
    TimeSpan OpenTime = default,
    RaceTimes? Race = null,
    MftOpenTimings? Open = null,
    PlanRun? Planning = null);
