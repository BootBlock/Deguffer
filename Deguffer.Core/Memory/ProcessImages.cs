namespace Deguffer.Core.Memory;

/// <summary>
/// The program one process runs, or why there is none: it has gone, or Windows would not say. The two
/// are different answers, because a process that would not answer may still be running the program
/// asked about.
/// </summary>
/// <param name="Path">The program's full path, where Windows gave it.</param>
/// <param name="Refused">
/// True where Windows would not answer, and false where the process has gone or its identifier now
/// belongs to a later one.
/// </param>
public readonly record struct ProcessImage(string? Path, bool Refused)
{
    public static ProcessImage Gone { get; } = new(null, Refused: false);

    public static ProcessImage Unanswered { get; } = new(null, Refused: true);

    public static ProcessImage Of(string path) => new(path, Refused: false);
}

/// <summary>
/// Asks Windows which program one process runs, for the process a snapshot named, and only that one.
/// </summary>
public interface IProcessImages
{
    /// <summary>
    /// The program the process created at <paramref name="creationTime"/> and holding
    /// <paramref name="processId"/> runs.
    /// </summary>
    ProcessImage ImageOf(int processId, long creationTime);
}

/// <inheritdoc />
/// <remarks>
/// One open per question, closed before the answer returns, with the identity decided first, as
/// <see cref="ProcessFactSource"/> decides it: a path read from the stranger now holding an identifier
/// would name the wrong program. A holder created later than the recorded time is a successor, so the
/// process asked about has gone; one created earlier says the recorded time is not this identifier's,
/// which is no answer at all.
/// </remarks>
public sealed class ProcessImages : IProcessImages
{
    public static ProcessImages Default { get; } = new(ProcessCalls.Instance);

    private readonly IProcessCalls _processes;

    internal ProcessImages(IProcessCalls processes) => _processes = processes;

    public ProcessImage ImageOf(int processId, long creationTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        var opening = _processes.Open(processId);

        if (opening.Process is not { } process)
        {
            return opening.Outcome is OpenOutcome.NotRunning ? ProcessImage.Gone : ProcessImage.Unanswered;
        }

        using (process)
        {
            if (process.CreationTime() is not { } created)
            {
                return ProcessImage.Unanswered;
            }

            if (created != creationTime)
            {
                return created > creationTime ? ProcessImage.Gone : ProcessImage.Unanswered;
            }

            return process.HasExited() switch
            {
                true => ProcessImage.Gone,
                false => process.ImagePath() is { } path ? ProcessImage.Of(path) : ProcessImage.Unanswered,
                null => ProcessImage.Unanswered,
            };
        }
    }
}
