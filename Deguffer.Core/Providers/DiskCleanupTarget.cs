namespace Deguffer.Core.Providers;

/// <summary>
/// One directory a provider has decided Windows' own Disk Cleanup handler should clear, before
/// anything has been measured. The counterpart of <see cref="DeletionTarget"/> for the route that is
/// not Deguffer's removal. See <see cref="Execution.DiskCleanupStep"/>.
/// </summary>
/// <param name="Path">The directory the row is about, in display form.</param>
/// <param name="Reason">Why it is disposable, written for the user.</param>
/// <param name="Handler">The handler's registered name.</param>
/// <param name="Volume">The top of the volume the handler is asked to clear, in display form.</param>
/// <param name="AlsoClears">
/// The other directories the handler is registered to clear and that are there, so the figure is
/// what the handler will take rather than what the row is named after.
/// </param>
/// <param name="LastWritten">§7's age. See <see cref="DeletionTarget.LastWritten"/>.</param>
/// <param name="RequiresElevation">Whether running the handler needs administrator rights.</param>
public sealed record DiskCleanupTarget(
    string Path,
    string Reason,
    string Handler,
    string Volume,
    IReadOnlyList<string> AlsoClears,
    DateTime? LastWritten,
    bool RequiresElevation);
