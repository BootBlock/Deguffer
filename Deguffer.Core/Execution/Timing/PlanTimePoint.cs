namespace Deguffer.Core.Execution.Timing;

/// <summary>
/// How long one provider took to plan in one full planning pass (#443), and when.
///
/// <para><b>A provider's id, a time and nothing that names a place</b>, for the reason
/// <see cref="Sizes.SizePoint"/> gives: the file can then never describe the user's folders.</para>
/// </summary>
/// <param name="TakenUtc">When the pass that timed it was kept, the moment it ended.</param>
/// <param name="Elapsed">How long its plan took (<see cref="ProviderPlanTime.Elapsed"/>).</param>
/// <param name="Elevated">
/// Whether the pass held administrator rights. Some rows only ask their slowest question with them,
/// the component store's analysis among them, so the two are different populations and are never
/// averaged together.
/// </param>
public readonly record struct PlanTimePoint(DateTime TakenUtc, TimeSpan Elapsed, bool Elevated);
