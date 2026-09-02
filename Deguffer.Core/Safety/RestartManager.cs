using System.Runtime.InteropServices;

namespace Deguffer.Core.Safety;

/// <param name="Holders">Display names of the processes holding the file open.</param>
/// <param name="Answered">
/// False if the Restart Manager could not be asked at all. An unanswered query is not an empty one:
/// the caller must not read it as "nothing holds this".
/// </param>
public readonly record struct FileHolders(IReadOnlyList<string> Holders, bool Answered)
{
    public static readonly FileHolders Unanswered = new([], Answered: false);
}

/// <summary>
/// Asks Windows which processes hold a file open, through the Restart Manager.
///
/// <para>This is the documented way to ask the question, and it needs no elevation — established
/// here rather than taken from the documentation: an unelevated process was told which process held
/// a file open, and named it correctly, before any of this shipped.</para>
///
/// <para><b>It answers for a file and refuses a directory.</b> <c>RmRegisterResources</c> returns
/// <c>ERROR_ACCESS_DENIED</c> for a directory path, so there is no way to ask "is anything under
/// this tree open" — which is why <see cref="LiveTreeInspector"/> pairs this with the process table
/// rather than relying on it alone, and why a provider has to name the file its tool locks.</para>
///
/// <para><b>It also refuses a path past <c>MAX_PATH</c>, in either form.</b> The plain path and the
/// extended-length one were both tried against a real file held open at 437 characters, and both
/// came back refused, so §6.3's usual answer does not apply here — there is no long-path route to
/// take. What that costs is stated rather than hidden: the query reports
/// <see cref="FileHolders.Unanswered"/>, the findings go incomplete, and the plan tells the user the
/// check could not run rather than that nothing is using the directory. A project that deep still
/// gets the two process-table signals, which have no such limit.</para>
///
/// <para>Declared with <c>DllImport</c> rather than the <c>LibraryImport</c> the rest of Core uses.
/// The source generator cannot express either of the two shapes this API needs: an array of strings
/// as a resource list, and a struct carrying fixed-length inline character buffers.</para>
/// </summary>
internal static class RestartManager
{
    private const int ErrorMoreData = 234;
    private const int MaximumAppName = 255;
    private const int MaximumServiceName = 63;

    /// <summary><c>CCH_RM_SESSION_KEY</c>: a GUID in registry form, without separators.</summary>
    private const int SessionKeyLength = 32;

    /// <summary>
    /// A ceiling on the processes reported for one query. The Restart Manager will happily describe
    /// every process on the machine for a file such as a system DLL, and a build directory that
    /// somehow reached that state is live several times over — the first few names are all the user
    /// needs to act.
    /// </summary>
    private const uint MaximumHolders = 32;

    /// <summary>
    /// Who holds <paramref name="files"/> open, in one session.
    ///
    /// A path that does not exist is not an error and reports nothing, which is what lets a caller
    /// pass a lock file that is only present while its tool is running.
    /// </summary>
    public static FileHolders Query(IReadOnlyList<string> files, CancellationToken ct = default)
    {
        if (files.Count == 0)
        {
            return new FileHolders([], Answered: true);
        }

        // The Win32 device prefix is rejected here: RmRegisterResources takes an ordinary path, and
        // a directory or a prefixed path both come back as ERROR_ACCESS_DENIED, which would look
        // like a refusal rather than like the wrong argument.
        var plain = new string[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            plain[i] = LongPath.Display(files[i]);
        }

        uint session = 0;
        var started = false;

        try
        {
            // Inside the guard rather than before it. This is the call that forces rstrtmgr.dll to
            // load, so it is the only one that can raise DllNotFoundException — outside the try, the
            // handlers below would be unreachable where they are needed and dead where they sit, and
            // a stripped image would take the whole planning pass down.
            if (RmStartSession(out session, 0, SessionKey()) != 0)
            {
                return FileHolders.Unanswered;
            }

            started = true;

            ct.ThrowIfCancellationRequested();

            if (RmRegisterResources(session, (uint)plain.Length, plain, 0, null, 0, null) != 0)
            {
                return FileHolders.Unanswered;
            }

            uint held = 0;
            var result = RmGetList(session, out var needed, ref held, null, out _);

            if (result == 0)
            {
                // Registered successfully and nothing holds any of them.
                return new FileHolders([], Answered: true);
            }

            if (result != ErrorMoreData || needed == 0)
            {
                return FileHolders.Unanswered;
            }

            held = Math.Min(needed, MaximumHolders);
            var info = new ProcessInfo[held];

            result = RmGetList(session, out _, ref held, info, out _);

            // ERROR_MORE_DATA again means the cap bit, and the array is filled: RmGetList reports it
            // whenever the buffer is smaller than the count it wants. Treating that as a failure
            // would turn the busiest directory on the disk — the one most obviously in use — into
            // the one Deguffer says it could not ask about.
            if (result is not (0 or ErrorMoreData))
            {
                return FileHolders.Unanswered;
            }

            var holders = new List<string>((int)held);
            for (var i = 0; i < held && i < info.Length; i++)
            {
                var name = info[i].ApplicationName;
                if (!string.IsNullOrWhiteSpace(name) && !holders.Contains(name, StringComparer.Ordinal))
                {
                    holders.Add(name);
                }
            }

            return new FileHolders(holders, Answered: true);
        }
        catch (DllNotFoundException)
        {
            // rstrtmgr.dll is present on every supported Windows, but a stripped image is not worth
            // taking the whole planning pass down for. Unanswered is the honest report.
            return FileHolders.Unanswered;
        }
        catch (EntryPointNotFoundException)
        {
            return FileHolders.Unanswered;
        }
        finally
        {
            if (started)
            {
                RmEndSession(session);
            }
        }
    }

    /// <summary>
    /// A buffer for the session key, which the Restart Manager <em>writes into</em> rather than
    /// reads. Passing a managed <c>string</c> here would hand native code an immutable object to
    /// write over: it happens to work while the value is exactly the length the API expects, which
    /// is how the mistake survives in samples, and it is undefined behaviour regardless.
    /// </summary>
    private static char[] SessionKey()
    {
        var key = new char[SessionKeyLength + 1];
        Guid.NewGuid().ToString("N").CopyTo(key);

        return key;
    }

    /// <summary>
    /// <c>RM_UNIQUE_PROCESS</c>. <c>ProcessStartTime</c> is a <c>FILETIME</c>, which is two 32-bit
    /// halves aligned to 4 — not an aligned 64-bit integer. Declaring it as <c>long</c> at default
    /// packing inserts four bytes of padding on x64 and shifts every string that follows, which
    /// showed up as process names missing their first two characters.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct UniqueProcess
    {
        public uint ProcessId;
        public uint StartTimeLow;
        public uint StartTimeHigh;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaximumAppName + 1)]
        public string ApplicationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaximumServiceName + 1)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint ApplicationStatus;
        public uint TerminalServicesSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int flags, [Out] char[] sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        string[]? files,
        uint applicationCount,
        UniqueProcess[]? applications,
        uint serviceCount,
        string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint needed,
        ref uint count,
        [In, Out] ProcessInfo[]? processes,
        out uint rebootReasons);
}
