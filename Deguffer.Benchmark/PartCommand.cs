using Deguffer.Core.Safety;

namespace Deguffer.Benchmark;

/// <summary>
/// A command the person running the race gives, run before each part of a round and outside its
/// time: one that empties the system file cache makes every part list from the drive, which is
/// what a cold result means.
///
/// <para>Deguffer never empties a memory list itself (§2), so the command is the person's own tool,
/// run through <c>cmd.exe</c> as they would type it, in the folder they ran the benchmark from. It is
/// started through <see cref="IProcessRunner"/>, so <c>cmd.exe</c> is found by its full path,
/// cancelling the run ends the command and whatever it started, and a command that fails stops the
/// run: a part is never reported as having followed a command that did not do its work.</para>
/// </summary>
internal sealed class PartCommand
{
    private readonly IProcessRunner _runner;
    private readonly string _shell;
    private readonly string _command;

    /// <param name="runner">What starts the shell.</param>
    /// <param name="shell"><c>cmd.exe</c>, by its full path.</param>
    /// <param name="command">The command as the person typed it.</param>
    public PartCommand(IProcessRunner runner, string shell, string command)
    {
        _runner = runner;
        _shell = shell;
        _command = command;
    }

    /// <summary><paramref name="command"/> on this machine, run from the current folder.</summary>
    public static PartCommand For(string command) => new(
        new ProcessRunner(Environment.CurrentDirectory),
        NativeSystemTool.In(SystemDirectories.Current, "cmd.exe"),
        command);

    /// <exception cref="PartCommandFailedException">The command could not be started, or ended with an exit code other than 0.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled, and the command was ended.</exception>
    public void Run(CancellationToken ct)
    {
        // /d skips the AutoRun commands the registry can add to every cmd.exe, so only the command
        // given runs. /s makes cmd.exe remove the outer pair of quotes added here and nothing else,
        // so the quotes inside reach it as typed.
        var outcome = _runner
            .RunAsync(_shell, $"/d /s /c \"{_command}\"", ToolRun.Command(CancelledCommand.Ended), ct)
            .GetAwaiter()
            .GetResult();

        // What the command wrote is dropped: it can name paths, and a result names none.
        if (!outcome.Succeeded)
        {
            throw new PartCommandFailedException($"The command run before each part ended with exit code {outcome.ExitCode}.");
        }
    }
}
