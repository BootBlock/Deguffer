namespace Deguffer.Core.Execution;

/// <param name="Ran">Whether Windows' handler ran its cleanup to the end.</param>
/// <param name="Message">
/// What happened, written for the user, or null where nothing needs saying. Never null where it did
/// not run: a handler refuses for reasons this code cannot fix, and the number it refused with is the
/// only thing that tells one from another.
/// </param>
public sealed record DiskCleanupOutcome(bool Ran, string? Message = null);

/// <summary>
/// Running one of the Disk Cleanup handlers Windows registers, behind an interface for the reason
/// <see cref="IRecycleBinEmptier"/> is one.
///
/// <para>The real handler deletes <c>Windows.old</c> on whatever machine runs it, so a test that
/// reached it would destroy the previous installation of whoever ran the suite. And §6.3 is a
/// requirement about the <em>form</em> of what crosses into Windows: the handler takes a volume in
/// display form, <c>C:\</c>, and no outcome of a cleanup can show which form crossed.</para>
/// </summary>
public interface IDiskCleanupHandlers
{
    /// <summary>
    /// Whether Windows registers a handler under <paramref name="handler"/> that this process can
    /// load, asked before a plan commits to it.
    ///
    /// <para>Here rather than only inside <see cref="Run"/> because the answer decides a
    /// <em>plan</em>: a step offered, sized and confirmed and then refused because the handler is
    /// missing would be a promise the preview could not keep. Deleting the path instead is not the
    /// fallback, because the handler is chosen for what it does besides deleting.</para>
    /// </summary>
    bool Serves(string handler);

    /// <param name="handler">
    /// The handler's name, as registered under
    /// <c>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches</c>.
    /// </param>
    /// <param name="volume">
    /// The top of the volume to clear, in display form. The caller supplies it and this does not
    /// derive it, so the assertion about what crosses stays falsifiable.
    /// </param>
    /// <param name="ct">
    /// Handed to the handler through its progress callback, which is the only way to stop one:
    /// answering a report with <c>E_ABORT</c> asks it to stop at its next check.
    /// </param>
    DiskCleanupOutcome Run(string handler, string volume, CancellationToken ct);
}
