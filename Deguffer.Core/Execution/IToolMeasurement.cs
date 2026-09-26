namespace Deguffer.Core.Execution;

/// <summary>
/// A tool's own figure for what its eviction command clears, asked again by the run once the command
/// has finished (§5.1).
///
/// <para><b>Why the run cannot measure the disk instead.</b> Most commands clear folders Deguffer can
/// read, so what they freed is the plan's figure less a fresh measurement of
/// <see cref="RunCommandStep.MeasuredPaths"/>. Some tools keep their cache where the signed-in account
/// may not look, or where a policy put it, and state its size themselves. Delivery Optimization is
/// the case: its cache is in the Network Service profile, which refuses an unelevated listing, and
/// Windows answers its size exactly. A disk measurement there would read the refused folder as holding
/// nothing and report the whole estimate as freed without having checked anything.</para>
///
/// <para>Implemented by the provider that knows how to ask its tool, so the run holds no knowledge of
/// any tool: it knows only that a command step may carry the question, and what to do with the
/// answer, which is the shape <see cref="IUseCheck"/> has.</para>
/// </summary>
public interface IToolMeasurement
{
    /// <summary>
    /// What the tool says its cache holds now, in bytes, or null where it would not say. Asked of the
    /// machine at the moment of the call, never from anything the planning pass remembered, because the
    /// answer is subtracted from a figure taken before the command ran.
    /// </summary>
    Task<long?> MeasureAsync(CancellationToken ct);
}
