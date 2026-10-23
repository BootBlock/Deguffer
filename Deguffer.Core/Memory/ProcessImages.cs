namespace Deguffer.Core.Memory;

/// <summary>
/// Asks Windows which program one process runs, for the process a snapshot named, and only that one.
/// </summary>
public interface IProcessImages
{
    /// <summary>
    /// The full path of the program the process created at <paramref name="creationTime"/> and holding
    /// <paramref name="processId"/> runs, or null where it has gone, its identifier now belongs to
    /// another process, or Windows would not say.
    /// </summary>
    string? ImagePathOf(int processId, long creationTime);
}

/// <inheritdoc />
/// <remarks>
/// One open per question, closed before the answer returns, with the identity decided first, as
/// <see cref="ProcessFactSource"/> decides it: a path read from the stranger now holding an identifier
/// would name the wrong program.
/// </remarks>
public sealed class ProcessImages : IProcessImages
{
    public static ProcessImages Default { get; } = new(ProcessCalls.Instance);

    private readonly IProcessCalls _processes;

    internal ProcessImages(IProcessCalls processes) => _processes = processes;

    public string? ImagePathOf(int processId, long creationTime)
    {
        if (processId <= 0 || _processes.Open(processId).Process is not { } process)
        {
            return null;
        }

        using (process)
        {
            return process.CreationTime() == creationTime && process.HasExited() is false ? process.ImagePath() : null;
        }
    }
}
