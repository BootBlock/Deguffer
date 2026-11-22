namespace Deguffer.Core.Diagnostics;

/// <summary>What one volume's index build did, and how its questions were answered (<see cref="IndexBuildLog"/>).</summary>
/// <param name="DriveLetter">The volume, in upper case.</param>
/// <param name="Started">
/// When its build opened the volume, or null where no build did: unelevated, or under the walk-only
/// route.
/// </param>
/// <param name="Ready">When its index was ready to answer, or null where the build ended without one or had not ended.</param>
/// <param name="AtLowPriority">How long the build read at low priority, up to its end or to now.</param>
/// <param name="TableAnswers">The questions about the volume the table answered.</param>
/// <param name="WalkAnswers">The questions about the volume a walk answered, whatever the reason it walked.</param>
internal readonly record struct IndexBuildTimes(
    char DriveLetter,
    TimeSpan? Started,
    TimeSpan? Ready,
    TimeSpan AtLowPriority,
    int TableAnswers,
    int WalkAnswers);
