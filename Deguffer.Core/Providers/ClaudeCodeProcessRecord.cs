using System.Globalization;
using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether the process a Claude Code record names is still running, asked of this machine.
///
/// <para>Three of Claude Code's files name a process by its id: an entry in the list of running
/// sessions, a messaging key, and an editor's handshake lock. None of them means anything until the
/// id is asked about, and each can be misread in the same two ways, so both answers are written
/// once.</para>
/// </summary>
internal static class ClaudeCodeProcessRecord
{
    private const string WindowsDomainPrefix = "win32:";

    /// <summary>The two fields a record may carry its process's creation time in, most specific first.</summary>
    private static readonly string[] StartFields = ["procStartFt", "procStart"];

    /// <summary>The largest FILETIME a <see cref="DateTime"/> can hold. Anything past it is not a creation time.</summary>
    private static readonly long MaximumFileTime = DateTime.MaxValue.ToFileTimeUtc();

    /// <summary>
    /// Whether an id recorded under <paramref name="pidDomain"/> names a process this machine's own
    /// process table can answer for.
    ///
    /// <para><b>An id is only an id inside the system that issued it.</b> Claude Code stamps a record
    /// with the process namespace it ran in: <c>win32:</c> and the machine's name in lower case on
    /// Windows, and a machine id with a namespace inside WSL, where the id names a Linux process.
    /// Probing a Linux id on Windows asks about whichever Windows process holds that number, and "not
    /// running" from that question would offer a live session's file.</para>
    ///
    /// <para>A record with no domain predates the field. Claude Code's own sweep of its messaging keys
    /// reads a missing domain as its own, and so does this.</para>
    ///
    /// <para><b>The comparison is with the name Windows reports to .NET, which is the NetBIOS
    /// name.</b> Claude Code writes the host name. The two can differ, most often where the host name
    /// is longer than fifteen characters, and that case answers false: the file is left alone.</para>
    /// </summary>
    public static bool IsThisMachine(string? pidDomain, IUserEnvironment environment) =>
        pidDomain is null
        || pidDomain.Equals(WindowsDomainPrefix + environment.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The process's state, told apart from a recycled id wherever the record wrote down when its
    /// process was created. See <see cref="ProcessLiveness.StateOfProcessStartedAt"/>.
    /// </summary>
    public static ProcessState StateOf(ProcessLiveness liveness, DateTimeOffset? recordedStart) =>
        recordedStart is { } start ? liveness.StateOfProcessStartedAt(start) : liveness.State;

    /// <summary>
    /// When the record says its process was created, or null where it does not say so in a form that
    /// can be trusted.
    ///
    /// <para><b>Parsed straight to a <see cref="long"/>, never through a floating-point number.</b>
    /// The value is a FILETIME near 1.3 × 10¹⁷, past what a double holds exactly. Read that way it can
    /// land a few ticks early, and one tick early reads as a later process holding the id — which is
    /// <see cref="ProcessState.NotRunning"/>, the answer that deletes.</para>
    ///
    /// <para><c>procStartFt</c> first, because its name says what it holds. <c>procStart</c> holds the
    /// same FILETIME on Windows, and a caller asks <see cref="IsThisMachine"/> before this, so a record
    /// written inside WSL, where <c>procStart</c> counts something else, never reaches here.</para>
    /// </summary>
    public static DateTimeOffset? RecordedStart(JsonElement record)
    {
        foreach (var field in StartFields)
        {
            if (BoundedJsonFile.StringProperty(record, field) is { } text
                && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var fileTime)
                && fileTime > 0
                && fileTime <= MaximumFileTime)
            {
                return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime));
            }
        }

        return null;
    }
}
