using System.Runtime.InteropServices;

namespace Deguffer.Core.Memory;

/// <summary>Why an open of one process identifier produced no handle, or that it produced one.</summary>
internal enum OpenOutcome
{
    /// <summary>Windows would not open it. Whatever that means, it is not evidence that the identifier is free.</summary>
    Refused,

    /// <summary>No process holds the identifier.</summary>
    NotRunning,

    Opened,
}

/// <summary>What an open got: a handle, or why there is none.</summary>
internal sealed record ProcessOpening(OpenOutcome Outcome, IOpenProcess? Process)
{
    public static readonly ProcessOpening NotRunning = new(OpenOutcome.NotRunning, null);

    public static readonly ProcessOpening Refused = new(OpenOutcome.Refused, null);

    public static ProcessOpening Of(IOpenProcess process) => new(OpenOutcome.Opened, process);
}

/// <summary>
/// What a token says about the process that holds it. Null in either member where Windows would not
/// say, which reaches <see cref="ProcessFacts"/> as <see cref="Answer.Unreadable"/> rather than as a
/// comparison that failed.
/// </summary>
/// <param name="User">The account, as its security identifier in string form.</param>
/// <param name="IntegrityLevel">
/// The last subauthority of the mandatory label, widened because Windows reports it unsigned.
/// </param>
internal readonly record struct TokenFacts(string? User, long? IntegrityLevel);

/// <summary>Whether a process belongs to an application package.</summary>
internal enum PackageIdentity
{
    Unreadable,
    NotPackaged,
    Packaged,
}

/// <summary>What Deguffer's own process is, for the facts that are comparisons against it.</summary>
internal readonly record struct OwnProcess(uint? SessionId, string? User, long? IntegrityLevel)
{
    /// <summary>Whether every member was read, which is what makes it worth keeping (G5).</summary>
    public bool Complete => SessionId is not null && User is not null && IntegrityLevel is not null;
}

/// <summary>
/// The Windows calls <see cref="ProcessFactSource"/> makes about a process, as a seam a test can drive.
///
/// <para>It is a seam rather than a direct call for the reason <see cref="IMemorySource"/> is one:
/// what this machine answers cannot be made into a process that has exited between two calls, a token
/// that will not open, or a package Windows has frozen.</para>
/// </summary>
internal interface IProcessCalls
{
    /// <summary>
    /// Open <paramref name="processId"/> for <c>PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE</c>,
    /// the pair §7.2.1 names and <see cref="Safety.ProcessProbe"/> established an unelevated Deguffer
    /// is granted for most processes.
    /// </summary>
    ProcessOpening Open(int processId);

    /// <summary>Deguffer's own session, account and integrity level.</summary>
    OwnProcess Own();
}

/// <summary>
/// One open process, for as long as the caller holds it. <b>Holding it is what makes every answer
/// below about the process that was opened</b>: an identifier "is valid from the time the process is
/// created until all handles to the process are closed", so nothing else can take it meanwhile
/// (§7.2.1).
/// </summary>
internal interface IOpenProcess : IDisposable
{
    /// <summary>When the kernel created it, as a FILETIME in UTC, or null where it would not be read.</summary>
    long? CreationTime();

    /// <summary>
    /// Whether it has exited, asked by waiting on it for no time at all. An exit code cannot answer it,
    /// because a process may exit with <c>STILL_ACTIVE</c>'s own value (§7.2.1).
    /// </summary>
    bool? HasExited();

    /// <summary>The session it runs in.</summary>
    uint? SessionId();

    /// <summary>Its account and integrity level.</summary>
    TokenFacts Token();

    /// <summary>Whether Windows would stop the machine if it ended.</summary>
    bool? IsCritical();

    PackageIdentity Package();

    /// <summary>Whether Windows has frozen it, which is how a suspended packaged application reads.</summary>
    bool? IsFrozen();
}

/// <inheritdoc />
internal sealed partial class ProcessCalls : IProcessCalls
{
    public static readonly ProcessCalls Instance = new();

    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x0010_0000;

    private const int ErrorInvalidParameter = 87;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;

    private ProcessCalls()
    {
    }

    public ProcessOpening Open(int processId)
    {
        var handle = OpenProcess(QueryLimitedInformation | Synchronize, false, (uint)processId);

        if (handle == 0)
        {
            // ERROR_INVALID_PARAMETER is the answer no process holds the identifier, measured rather
            // than documented: Safety.ProcessProbe records that measurement and what each other
            // refusal was found to mean.
            return Marshal.GetLastPInvokeError() == ErrorInvalidParameter
                ? ProcessOpening.NotRunning
                : ProcessOpening.Refused;
        }

        return ProcessOpening.Of(new HeldProcess(handle, processId));
    }

    /// <summary>
    /// The account comes from the same token as the integrity level rather than from
    /// <see cref="Safety.IUserEnvironment.UserSecurityIdentifier"/>, which carries it for
    /// <c>$Recycle.Bin</c>: one token read gives one consistent answer for a comparison that needs
    /// both, and the session and the integrity level are not on that seam at all.
    /// </summary>
    public OwnProcess Own()
    {
        // The pseudo-handle for this process, which needs no closing.
        var token = ProcessToken.Read(GetCurrentProcess());

        return new OwnProcess(SessionOf(Environment.ProcessId), token.User, token.IntegrityLevel);
    }

    private static uint? SessionOf(int processId) =>
        ProcessIdToSessionId((uint)processId, out var session) ? session : null;

    private sealed class HeldProcess(nint handle, int processId) : IOpenProcess
    {
        public long? CreationTime() =>
            GetProcessTimes(handle, out var created, out _, out _, out _) ? created : null;

        public bool? HasExited() => WaitForSingleObject(handle, 0) switch
        {
            WaitObject0 => true,
            WaitTimeout => false,
            _ => null,
        };

        /// <summary>
        /// By identifier rather than through the handle, as §7.2.1 has it, which is safe only because
        /// this handle is open: nothing else can hold the identifier while it is.
        /// </summary>
        public uint? SessionId() => SessionOf(processId);

        public TokenFacts Token() => ProcessToken.Read(handle);

        public bool? IsCritical() => IsProcessCritical(handle, out var critical) ? critical : null;

        /// <summary>
        /// Asked with no buffer, because whether it has a package identity is the whole question: a
        /// packaged process answers that the buffer is too small for its name, and one without a
        /// package identity says so.
        /// </summary>
        public PackageIdentity Package()
        {
            uint length = 0;

            return GetPackageFullName(handle, ref length, 0) switch
            {
                AppModelErrorNoPackage => PackageIdentity.NotPackaged,
                0 or ErrorInsufficientBuffer => PackageIdentity.Packaged,
                _ => PackageIdentity.Unreadable,
            };
        }

        public bool? IsFrozen() => ProcessSnapshot.IsFrozen(handle);

        public void Dispose() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint session);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessCritical(nint process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [LibraryImport("kernel32.dll", EntryPoint = "GetPackageFullName")]
    private static partial int GetPackageFullName(nint process, ref uint length, nint name);
}
