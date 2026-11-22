namespace Deguffer.Core.Execution;

/// <summary>
/// What a tool writes into an item it has been told to remove, where it removes the item later
/// rather than at once.
///
/// <para><b>LM Studio on Windows is the case.</b> Its <c>lms runtime remove</c> writes a
/// <c>MARKED_FOR_DELETION</c> file into the runtime's folder and returns, and LM Studio deletes the
/// folder the next time it starts. So the run frees nothing it can measure, and the step's figure is reported as scheduled rather than
/// reclaimed. The marker is also the only evidence that the command did anything: the tool reports
/// success before its own removal has run.</para>
/// </summary>
/// <param name="Marker">The file the tool writes directly inside the item.</param>
/// <param name="Remover">What will carry the removal out, named for the user.</param>
public sealed record ScheduledRemoval(string Marker, string Remover);
