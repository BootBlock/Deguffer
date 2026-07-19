using Deguffer.Core.Safety;

namespace Deguffer.Core.Diagnostics;

/// <summary>
/// Appends unhandled exceptions to <c>%LOCALAPPDATA%\Deguffer\crash.log</c>.
///
/// The XAML runtime terminates the process at the point an exception escapes a framework callback
/// (<c>0xc000027b</c>), and the Windows Application log records only the faulting module. Unless
/// something writes the detail out before the process dies, there is nothing left to diagnose it
/// from.
///
/// This lives in Core rather than the app so the formatting and the failure paths are testable
/// against a fake environment — none of it is reachable through the WinUI shell.
/// </summary>
public sealed class CrashLog
{
    /// <summary>
    /// The point at which the file is restarted rather than appended to. A crash loop writes an
    /// entry per launch, and a tool whose premise is freeing disk space must not quietly consume
    /// it. Sized to hold many entries — only the recent ones are worth anything anyway.
    /// </summary>
    private const long MaximumBytes = 256 * 1024;

    private readonly Lock _writeGate = new();
    private readonly string _directory;

    public CrashLog(IUserEnvironment environment)
    {
        _directory = Path.Combine(environment.LocalAppData, "Deguffer");
        FilePath = Path.Combine(_directory, "crash.log");
    }

    /// <summary>Where entries are written.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Record <paramref name="exception"/>, labelled with the <paramref name="source"/> handler
    /// that caught it. Returns whether it reached disk.
    ///
    /// Nothing here throws, by design, and there are deliberately no argument guards: this runs
    /// while the process is already being torn down, and an exception raised from here would
    /// replace a diagnosable fault with an undiagnosable one. <paramref name="exception"/> is
    /// nullable for the same reason — WinUI can hand a handler an event argument whose exception
    /// failed to marshal, and "something faulted, detail unavailable" is still worth more than an
    /// empty file.
    /// </summary>
    public bool Record(string source, Exception? exception)
    {
        // An empty LocalAppData leaves a relative path, which LongPath.Extended would resolve
        // against the working directory — dropping the log next to the executable, or wherever the
        // process happened to be started from. Better to report that nothing was recorded than to
        // leave it somewhere nobody will look.
        if (!System.IO.Path.IsPathRooted(FilePath))
        {
            return false;
        }

        try
        {
            // Handlers fire on whichever thread faulted, and UnobservedTaskException arrives on
            // the finalizer thread — two entries can genuinely race.
            lock (_writeGate)
            {
                Directory.CreateDirectory(LongPath.Extended(_directory));

                var file = LongPath.Extended(FilePath);
                if (Oversized(file))
                {
                    File.Delete(file);
                }

                File.AppendAllText(file, Entry(source, exception));
                return true;
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException
                or ArgumentException or System.Security.SecurityException)
        {
            // ArgumentException is here because it is the one failure that arrives before any I/O
            // is attempted — LongPath.Extended rejects a malformed path outright — and a handler
            // running inside a crash cannot afford to let it escape.
            return false;
        }
    }

    private static bool Oversized(string extendedPath)
    {
        var file = new FileInfo(extendedPath);
        return file.Exists && file.Length >= MaximumBytes;
    }

    /// <summary>
    /// <see cref="Exception.ToString"/> already carries the type, message, stack and every inner
    /// exception; the timestamp and source are what the Application log entry lacks.
    /// </summary>
    private static string Entry(string source, Exception? exception) =>
        $"""

        ===== {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC — {source} =====
        {exception?.ToString() ?? "No exception object was supplied by the handler."}

        """;
}
