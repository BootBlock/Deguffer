using System.Collections;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace Deguffer.Core.Safety;

/// <summary>
/// The ambient machine, behind an interface so provider rules are testable against a temp
/// directory rather than the developer's real profile.
/// </summary>
public interface IUserEnvironment
{
    /// <summary><c>%USERPROFILE%</c>.</summary>
    string UserProfile { get; }

    /// <summary><c>%LOCALAPPDATA%</c>.</summary>
    string LocalAppData { get; }

    /// <summary><c>%APPDATA%</c>.</summary>
    string RoamingAppData { get; }

    /// <summary>
    /// <c>%USERPROFILE%\AppData\LocalLow</c>, or null when Windows will not say where it is.
    ///
    /// <para>The third application-data tier, the one a program running at low integrity writes to
    /// because it may not write to the other two. NVIDIA's driver keeps a second shader cache here,
    /// measured at much the same size as the one under <c>%LOCALAPPDATA%</c> and a genuinely
    /// separate tree rather than a link to it.</para>
    ///
    /// <para><b>Null is a real answer and must stay one.</b> There is no
    /// <see cref="Environment.SpecialFolder"/> for this tier, so it comes from
    /// <c>SHGetKnownFolderPath</c>, which fails rather than returning a path when no user profile
    /// is loaded. §5.2 forbids guessing at a location, so a caller that was not told where LocalLow
    /// is targets nothing under it.</para>
    /// </summary>
    string? LocalLowAppData { get; }

    /// <summary>The per-user temp directory — NuGet keeps <c>NuGetScratch</c> here.</summary>
    string TempPath { get; }

    /// <summary>
    /// This user's Windows security identifier, or null if it cannot be established.
    ///
    /// Exists because a per-volume <c>$Recycle.Bin</c> is divided into one directory per account,
    /// named by SID, and telling this user's from another's is the whole of §5.2 there. Null is a
    /// real answer and must stay one: a provider that cannot identify the user recognises no child
    /// at all, which is the direction §5.2 requires the unknown case to fail in.
    /// </summary>
    string? UserSecurityIdentifier { get; }

    /// <summary>
    /// This user's Windows account name — the <c>UserName</c> half of <c>DOMAIN\UserName</c>.
    ///
    /// <para>Exists because a File History target is divided by account name and then by machine
    /// name, so one drive can hold several people's histories and several machines' histories of one
    /// person. Telling this user's from the rest is §5.2 there, exactly as
    /// <see cref="UserSecurityIdentifier"/> is inside a <c>$Recycle.Bin</c>.</para>
    ///
    /// <para>Not derived from <see cref="UserProfile"/>, which is the profile <em>folder</em>: the
    /// two agree on most machines and diverge on any account renamed after it was created, and on
    /// one whose profile folder collided with an existing one when it was made.</para>
    /// </summary>
    string UserName { get; }

    /// <summary>
    /// This machine's NetBIOS name, for the same reason <see cref="UserName"/> is here: it is the
    /// second level of a File History target's layout, and a target that has served two machines
    /// holds a folder for each.
    /// </summary>
    string MachineName { get; }

    /// <summary>
    /// Resolve an executable on <c>PATH</c>, or null if it is not installed.
    ///
    /// <para>The <c>PATH</c> searched is the one the machine has now, not the one this process was
    /// started with. <see cref="Invalidate"/> says how the two come to differ.</para>
    /// </summary>
    string? FindExecutable(string command);

    /// <summary>
    /// Read an environment variable, or null if it is unset.
    ///
    /// Exists because several tools relocate their cache through one — <c>PLAYWRIGHT_BROWSERS_PATH</c>
    /// is the first — and §5.2's "never assume a location" applies to the root just as much as to the
    /// children beneath it.
    ///
    /// <para>Answered from the machine as it now stands rather than from this process's own block,
    /// for the reason <see cref="Invalidate"/> gives. A variable the launching process set to
    /// something of its own keeps that value for the session, because that is a deliberate choice
    /// about where this run should look.</para>
    /// </summary>
    string? GetEnvironmentVariable(string name);

    /// <summary>
    /// Read a string value from under <c>HKEY_CURRENT_USER</c>, or null when the key, the value or
    /// the permission to read it is missing.
    ///
    /// <para>Exists because Steam records where it is installed and nothing else on disk does. The
    /// install directory is not under the profile, it moves with the user's game library, and §5.2's
    /// "never assume a location" forbids guessing at <c>%PROGRAMFILES(X86)%\Steam</c>.</para>
    ///
    /// <para><b>The current user's hive only, and deliberately so.</b> The machine-wide key is
    /// redirected under <c>WOW6432Node</c> for a 64-bit process, which is a second thing to get
    /// wrong for an answer about an install this account may never have run. Steam's own client
    /// writes the per-user value every time it starts, so a user with a Steam cache to reclaim has
    /// it.</para>
    /// </summary>
    /// <param name="keyPath">The key, relative to <c>HKEY_CURRENT_USER</c>.</param>
    /// <param name="valueName">The value to read.</param>
    string? ReadCurrentUserRegistryValue(string keyPath, string valueName);

    /// <summary>
    /// Discard what was read from the machine, so the next look sees the environment as it now
    /// stands. Called at the start of a planning pass, and implicit in the fresh instance each
    /// Explore policy build constructs, so a toolchain installed while the app was open is picked
    /// up by both pages.
    ///
    /// <para><b>Windows never pushes an environment change into a running process.</b> An installer
    /// that adds its directory to <c>PATH</c>, or that relocates a cache through a variable, writes
    /// the registry and broadcasts <c>WM_SETTINGCHANGE</c>; the block this process was handed at
    /// start-up does not move. Dropping a cached lookup alone would therefore search the same stale
    /// <c>PATH</c> again, so this reads the machine and user environment keys afresh as well.</para>
    /// </summary>
    void Invalidate();
}

/// <inheritdoc />
public sealed partial class UserEnvironment : IUserEnvironment
{
    public static readonly UserEnvironment Current = new();

    private readonly Func<IReadOnlyDictionary<string, string>> _readMachine;
    private readonly Func<IReadOnlyDictionary<string, string>> _readUser;

    /// <summary>
    /// The environment this process was given, and the one every refresh is composed over. Kept
    /// because a refresh is the start-up block plus the registry as it is at that moment, never the
    /// previous refresh plus it.
    /// </summary>
    private readonly EnvironmentBlock _startup;

    private readonly Lock _gate = new();

    /// <summary>
    /// The composed environment, or null when <see cref="Invalidate"/> has dropped it and nothing
    /// has asked since. Rebuilt lazily rather than inside <see cref="Invalidate"/>, because the
    /// planner invalidates every provider before any of them plans and they share this instance:
    /// building on demand collapses that burst of calls into the one registry read the pass needs.
    /// </summary>
    private volatile EnvironmentBlock? _block;

    public UserEnvironment()
        : this(ReadMachineEnvironment, ReadUserEnvironment, ProcessEnvironment())
    {
    }

    /// <param name="readMachine">
    /// Where <c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment</c> is read from,
    /// unexpanded. Injected so a test can change the machine between two passes, which is the whole
    /// of what this class does that is worth asserting and is not something to do to a real
    /// machine (G8).
    /// </param>
    /// <param name="readUser"><c>HKCU\Environment</c>, read the same way.</param>
    /// <param name="process">This process's own environment block.</param>
    internal UserEnvironment(
        Func<IReadOnlyDictionary<string, string>> readMachine,
        Func<IReadOnlyDictionary<string, string>> readUser,
        IReadOnlyDictionary<string, string> process)
    {
        _readMachine = readMachine;
        _readUser = readUser;
        _startup = EnvironmentBlock.Startup(process, readMachine(), readUser());
    }

    public string UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string LocalAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string RoamingAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string? LocalLowAppData { get; } = ResolveLocalLow();

    public string TempPath { get; } = Path.GetTempPath();

    // Read once rather than through Invalidate: a process cannot change the account it runs as,
    // and relaunching elevated makes a new process with the same identity.
    public string? UserSecurityIdentifier { get; } = WindowsIdentity.GetCurrent().User?.Value;

    public string UserName { get; } = Environment.UserName;

    public string MachineName { get; } = Environment.MachineName;

    public void Invalidate() => _block = null;

    /// <summary>
    /// The environment as it now stands, composed once and kept until <see cref="Invalidate"/>
    /// drops it. Everything derived from it — where each command resolved included — goes with it
    /// in the same reference swap.
    /// </summary>
    private EnvironmentBlock Block
    {
        get
        {
            if (_block is { } composed)
            {
                return composed;
            }

            lock (_gate)
            {
                return _block ??= _startup.Refresh(_readMachine(), _readUser());
            }
        }
    }

    public string? GetEnvironmentVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Block.Value(name);
    }

    /// <summary>
    /// Not memoised: the one caller memoises the answer it derives for the life of a planning pass,
    /// and a second cache here would be a second thing for <see cref="Invalidate"/> to get wrong.
    /// </summary>
    public string? ReadCurrentUserRegistryValue(string keyPath, string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);

            // Anything that is not a string is not an answer to this question. A REG_DWORD where a
            // path was expected would otherwise arrive as its decimal digits and be treated as one.
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // A hive this account may not read, and a key already marked for deletion. Both are
            // ordinary on a long-lived machine, and both mean the same thing here: nothing said
            // where the tool is.
            return null;
        }
    }

    public string? FindExecutable(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var block = Block;

        // Negative results are kept too, which is what makes reading the registry again matter: a
        // tool that was absent stays absent to this block however often it is asked about.
        return block.Resolved.GetOrAdd(command, name => Locate(block, name));
    }

    private static string? Locate(EnvironmentBlock block, string command)
    {
        foreach (var directory in block.PathDirectories)
        {
            foreach (var candidate in Candidates(directory, command, block.PathExtensions))
            {
                if (LongPath.FileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary><c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment</c>.</summary>
    private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    private static IReadOnlyDictionary<string, string> ReadMachineEnvironment() =>
        ReadEnvironmentKey(Registry.LocalMachine, MachineEnvironmentKey);

    private static IReadOnlyDictionary<string, string> ReadUserEnvironment() =>
        ReadEnvironmentKey(Registry.CurrentUser, "Environment");

    /// <summary>
    /// Every string value under one of the two environment keys, left exactly as it was written.
    ///
    /// <para><c>DoNotExpandEnvironmentNames</c> is the point of reading it here at all: without it
    /// the framework resolves a <c>REG_EXPAND_SZ</c> value's <c>%NAME%</c> against
    /// <em>this process's</em> environment, which is the stale block the refresh exists to get away
    /// from. <see cref="EnvironmentBlock"/> expands them against the composed set instead.</para>
    ///
    /// <para>An unreadable key answers with nothing rather than failing. The machine key is
    /// world-readable on an ordinary Windows install, so this is the locked-down-machine case, and
    /// answering with nothing leaves the process's own environment standing.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadEnvironmentKey(RegistryKey hive, string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var key = hive.OpenSubKey(path);

            if (key is null)
            {
                return values;
            }

            foreach (var name in key.GetValueNames())
            {
                // The key's unnamed default value names no variable, and anything that is not a
                // string is not an environment value.
                if (name.Length > 0 &&
                    key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value)
                {
                    values[name] = value;
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // A hive this account may not read, and a key already marked for deletion — the same
            // two ordinary failures ReadCurrentUserRegistryValue handles, meaning the same thing.
        }

        return values;
    }

    private static IReadOnlyDictionary<string, string> ProcessEnvironment()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                values[name] = value;
            }
        }

        return values;
    }

    /// <summary><c>KF_FLAG_DONT_VERIFY</c>.</summary>
    private const uint DoNotVerify = 0x00004000;

    /// <summary>
    /// Ask Windows where LocalLow is, once, when the environment is constructed.
    ///
    /// <para>Verification is switched off because the question is where the tier <em>is</em>, not
    /// whether it exists yet. A caller decides what to do about an absent directory by looking for
    /// the cache it wants, and without this flag a profile that has never had a low-integrity
    /// program run in it would answer identically to a platform that could not say at all.</para>
    ///
    /// <para><b><c>FOLDERID_LocalAppDataLow</c> is built here rather than held in a static
    /// field.</b> This runs from an instance initialiser, and <see cref="Current"/> is a static
    /// field declared above any such field would be. Static initialisers run in textual order, so
    /// the identifier would still be <see cref="Guid.Empty"/> when the singleton constructs itself,
    /// the call would fail, and the one environment the application actually uses would report
    /// LocalLow as unknown while a freshly constructed one answered correctly.</para>
    /// </summary>
    private static string? ResolveLocalLow()
    {
        var folderId = new Guid("a520a1a4-1780-4ff6-bd18-167343c5af16");
        var result = SHGetKnownFolderPath(in folderId, DoNotVerify, IntPtr.Zero, out var buffer);

        try
        {
            return result == 0 ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            // The buffer is the caller's to release whether or not the call succeeded, which is
            // what the documented contract says and is why this covers the failure path too.
            // Releasing IntPtr.Zero is a no-op, so it costs nothing when there was no buffer.
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(
        in Guid folderId,
        uint flags,
        IntPtr token,
        out IntPtr path);

    private static IEnumerable<string> Candidates(
        string directory,
        string command,
        IReadOnlyList<string> extensions)
    {
        // A malformed PATH entry is normal on a long-lived machine; skip it rather than failing
        // the whole scan.
        string baseName;
        try
        {
            baseName = Path.Combine(directory, command);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        if (Path.HasExtension(command))
        {
            yield return baseName;
            yield break;
        }

        foreach (var extension in extensions)
        {
            yield return baseName + extension;
        }
    }
}
