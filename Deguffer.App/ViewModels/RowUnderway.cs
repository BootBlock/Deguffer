using Deguffer.Core.Execution;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The row a clean is on, and what it is doing there. One value rather than two properties, so the run
/// reaching the same row again to do something else (a §5.6 proof after its removal) is a change a
/// listener hears, as reaching another row is.
/// </summary>
/// <param name="Row">The row the plan belongs to.</param>
/// <param name="Stage">Whether the run is removing anything from it, or only checking it.</param>
public sealed record RowUnderway(FindingViewModel Row, PlanStage Stage);
