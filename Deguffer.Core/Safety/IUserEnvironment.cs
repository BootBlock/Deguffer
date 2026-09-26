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

    /// <summary>
    /// The user's Videos folder, wherever it has been moved to, or null when Windows will not say.
    ///
    /// <para>DaVinci Resolve writes its render cache here when no Media Storage location is set. It is
    /// read from the known folder rather than composed from <see cref="UserProfile"/>, because Windows
    /// and OneDrive both move it, and a composed path would look in a folder Resolve never wrote to.</para>
    /// </summary>
    string? Videos { get; }

    /// <summary>
    /// The user's Documents folder, wherever it has been moved to, or null when Windows will not say.
    ///
    /// <para>PCSX2 keeps its data here, and older Dolphin installs do too. Read from the known folder
    /// rather than composed from <see cref="UserProfile"/> for the reason <see cref="Videos"/> is:
    /// Windows and OneDrive both move it, and each emulator asks Windows for it.</para>
    /// </summary>
    string? Documents { get; }

    /// <summary>
    /// The folders Windows gives this account for its own files — Desktop, Documents, Downloads,
    /// Music, Pictures, Videos, Saved Games and OneDrive — wherever each has been moved to. A folder
    /// Windows will not name is left out rather than guessed.
    ///
    /// <para>Read from the known folders rather than composed from <see cref="UserProfile"/>, for the
    /// reason <see cref="Videos"/> is: Windows and OneDrive both move them, and a folder a setting
    /// names at the moved location is still the account's own. <c>StandingFolders</c> refuses every
    /// one of them as a target, and adds the locations in the profile that they have by default.</para>
    /// </summary>
    IReadOnlyList<string> PersonalFolders { get; }

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
    /// <para>The <c>PATH</c> searched is the one this process started with, extended by the one the
    /// machine has now. A directory it gained since start-up is searched; a directory it had at
    /// start-up is searched whether or not the machine still lists it, because taking one away
    /// would remove a tool rather than add one. <see cref="Invalidate"/> says why the two differ at
    /// all.</para>
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
    /// The names of the keys directly under a key in <c>HKEY_CURRENT_USER</c>, or none when the key
    /// or the permission to read it is missing.
    ///
    /// <para>Exists because Adobe files its media cache settings under a key whose name carries a
    /// version, such as <c>Common 13.0</c>, and the version moves between releases. Guessing at the
    /// names would miss a release nobody listed, and a location that is missed is a cache that is
    /// never found rather than a wrong deletion, so the names are read instead.</para>
    /// </summary>
    /// <param name="keyPath">The key, relative to <c>HKEY_CURRENT_USER</c>.</param>
    IReadOnlyList<string> ReadCurrentUserRegistrySubKeyNames(string keyPath);

    /// <summary>
    /// Read a string value from under <c>HKEY_LOCAL_MACHINE</c> in the named registry view, or null
    /// when the key, the value or the permission to read it is missing.
    ///
    /// <para>Exists because Jellyfin's installer records where the server keeps its data here and
    /// nowhere else, and the user chooses that folder during the install. The installer is a 32-bit
    /// program, so it writes the key in the 32-bit view, which a 64-bit process reads only by asking
    /// for that view. <see cref="ReadCurrentUserRegistryValue"/> gives why the machine-wide hive is
    /// otherwise avoided: here it is the only record there is.</para>
    ///
    /// <para>A <c>REG_EXPAND_SZ</c> value is expanded against this process's environment, which is
    /// the environment of the account the installer ran as for the variables such a value names.</para>
    /// </summary>
    /// <param name="keyPath">The key, relative to <c>HKEY_LOCAL_MACHINE</c>.</param>
    /// <param name="valueName">The value to read.</param>
    /// <param name="view">The view the program that wrote the key writes to.</param>
    string? ReadLocalMachineRegistryValue(string keyPath, string valueName, RegistryView view);

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
    /// <summary>
    /// The environment this <em>process</em> started with, composed once for the whole process.
    ///
    /// <para><b>Shared rather than worked out per instance, and that is the point.</b> Which
    /// variables the launching process set differently is decided by comparing its block against
    /// the registry, and that comparison only means what it says while the registry still holds
    /// what it held at launch. Explore builds a fresh <see cref="UserEnvironment"/> at every policy
    /// build, long after start-up: an instance that read the registry for itself would see a
    /// relocation an installer had since made, read the difference as a deliberate choice by the
    /// launching shell, and pin the variable to the value that relocation moved away from — this
    /// class's own defect, reintroduced on the page it most matters to.</para>
    ///
    /// <para>Declared above <see cref="Current"/> deliberately. Static initialisers run in textual
    /// order, so a field below it would still be null while the singleton constructed itself, for
    /// the reason <see cref="ResolveLocalLow"/> sets out at greater length.</para>
    /// </summary>
    private static readonly EnvironmentBlock ProcessStartup =
        EnvironmentBlock.Startup(ProcessEnvironment(), ReadMachineEnvironment(), ReadUserEnvironment());

    public static readonly UserEnvironment Current = new();

    private readonly Func<IReadOnlyDictionary<string, EnvironmentValue>> _readMachine;
    private readonly Func<IReadOnlyDictionary<string, EnvironmentValue>> _readUser;

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
        : this(ReadMachineEnvironment, ReadUserEnvironment, ProcessStartup)
    {
    }

    /// <param name="readMachine">
    /// Where <c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment</c> is read from,
    /// unexpanded and with each value's kind. Injected so a test can change the machine between two
    /// passes, which is the whole of what this class does that is worth asserting and is not
    /// something to do to a real machine (G8).
    /// </param>
    /// <param name="readUser"><c>HKCU\Environment</c>, read the same way.</param>
    /// <param name="startup">
    /// The environment the process started with. Taken as an argument rather than composed here,
    /// because composing it needs the registry <em>as it was then</em> — see
    /// <see cref="ProcessStartup"/>.
    /// </param>
    internal UserEnvironment(
        Func<IReadOnlyDictionary<string, EnvironmentValue>> readMachine,
        Func<IReadOnlyDictionary<string, EnvironmentValue>> readUser,
        EnvironmentBlock startup)
    {
        _readMachine = readMachine;
        _readUser = readUser;
        _startup = startup;
    }

    public string UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string LocalAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string RoamingAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string? LocalLowAppData { get; } = ResolveLocalLow();

    public string? Videos { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) is { Length: > 0 } videos ? videos : null;

    public string? Documents { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } documents ? documents : null;

    public IReadOnlyList<string> PersonalFolders { get; } = ResolvePersonalFolders();

    /// <summary>
    /// Resolved as Windows resolves it — <c>TMP</c>, then <c>TEMP</c>, then whatever
    /// <see cref="Path.GetTempPath"/> falls back to — from the environment as it now stands.
    ///
    /// <para><b>It has to move with the variables, not sit beside them.</b>
    /// <c>ProtectedRegions</c> refuses this one path as the temporary folder, while
    /// <c>TempRoots</c> accepts what <c>TMP</c> and <c>TEMP</c> name. Once those two answer from
    /// the machine and this did not, a folder redirected while Deguffer was open would be a scratch
    /// root Storage empties and an ordinary removable folder in Explore at the same moment
    /// (§5.3, §7.1).</para>
    ///
    /// <para>A value that is not fully qualified is no answer, so <see cref="LongPath.Configured"/>
    /// declines it and the next candidate is taken. §5.2 fails towards knowing nothing.</para>
    /// </summary>
    public string TempPath =>
        LongPath.Configured(Block.Value("TMP"))
        ?? LongPath.Configured(Block.Value("TEMP"))
        ?? Path.GetTempPath();

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
    /// Not memoised, so no answer here can outlive <see cref="Invalidate"/>. A caller that needs one
    /// for the life of a planning pass keeps what it derives from it.
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

    /// <summary>
    /// Not memoised, for the reason <see cref="ReadCurrentUserRegistryValue"/> is not.
    /// </summary>
    public IReadOnlyList<string> ReadCurrentUserRegistrySubKeyNames(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);

            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // The same two ordinary failures ReadCurrentUserRegistryValue handles, meaning the same
            // thing: nothing said where the tool is.
            return [];
        }
    }

    /// <summary>
    /// Not memoised: the one caller memoises the layout it derives for the life of a planning pass, and
    /// a second cache here would be a second thing for <see cref="Invalidate"/> to get wrong.
    /// </summary>
    public string? ReadLocalMachineRegistryValue(string keyPath, string valueName, RegistryView view)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hive.OpenSubKey(keyPath);

            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // The same two ordinary failures ReadCurrentUserRegistryValue handles, meaning the same
            // thing: nothing said where the tool is.
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

    internal static IReadOnlyDictionary<string, EnvironmentValue> ReadMachineEnvironment() =>
        ReadEnvironmentKey(Registry.LocalMachine, MachineEnvironmentKey);

    internal static IReadOnlyDictionary<string, EnvironmentValue> ReadUserEnvironment() =>
        ReadEnvironmentKey(Registry.CurrentUser, "Environment");

    /// <summary>
    /// Every string value under one of the two environment keys, left exactly as it was written and
    /// carrying the kind that says whether Windows would expand it.
    ///
    /// <para><c>DoNotExpandEnvironmentNames</c> is the point of reading it here at all: without it
    /// the framework resolves a <c>REG_EXPAND_SZ</c> value's <c>%NAME%</c> against
    /// <em>this process's</em> environment, which is the stale block the refresh exists to get away
    /// from. <see cref="EnvironmentBlock"/> expands them against the composed set instead.</para>
    ///
    /// <para><b>The kind comes with the value, because that option suppresses the one thing that
    /// tells the two apart.</b> Windows expands a <c>REG_EXPAND_SZ</c> value and passes a
    /// <c>REG_SZ</c> one to a program as written, so reading both unexpanded and then expanding
    /// both puts Deguffer and the tool on different directories. See
    /// <see cref="EnvironmentValue"/>.</para>
    ///
    /// <para>An unreadable key answers with nothing rather than failing. The machine key is
    /// world-readable on an ordinary Windows install, so this is the locked-down-machine case, and
    /// answering with nothing leaves the process's own environment standing.</para>
    ///
    /// <para>Reachable by the suite, rather than private, because the kind is decided here and
    /// nowhere else: <see cref="EnvironmentBlock"/> can be handed either kind directly, but that
    /// a <c>REG_SZ</c> value is <em>read</em> as one is only assertable against a real key, and
    /// the two keys this names are not keys a suite may write to (G8).</para>
    /// </summary>
    /// <param name="hive">The root the key is opened under.</param>
    /// <param name="path">The key's path within that hive.</param>
    internal static IReadOnlyDictionary<string, EnvironmentValue> ReadEnvironmentKey(RegistryKey hive, string path)
    {
        var values = new Dictionary<string, EnvironmentValue>(StringComparer.OrdinalIgnoreCase);

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
                if (name.Length == 0 ||
                    key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value)
                {
                    continue;
                }

                values[name] = new EnvironmentValue(value, key.GetValueKind(name) is RegistryValueKind.ExpandString);
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

    /// <summary>Ask Windows where LocalLow is, once, when the environment is constructed.</summary>
    private static string? ResolveLocalLow() => KnownFolder("a520a1a4-1780-4ff6-bd18-167343c5af16");

    /// <summary>
    /// Ask Windows where the account's own folders are, once, when the environment is constructed:
    /// <c>FOLDERID_Desktop</c>, <c>_Documents</c>, <c>_Downloads</c>, <c>_Music</c>, <c>_Pictures</c>,
    /// <c>_Videos</c>, <c>_SavedGames</c> and <c>_SkyDrive</c>, the last being OneDrive's folder.
    /// </summary>
    private static IReadOnlyList<string> ResolvePersonalFolders() =>
    [
        .. new[]
        {
            "b4bfcc3a-db2c-424c-b029-7fe99a87c641",
            "fdd39ad0-238f-46af-adb4-6c85480369c7",
            "374de290-123f-4565-9164-39c4925e467b",
            "4bd8d571-6d19-48d3-be97-422220080e43",
            "33e28130-4e1e-4676-835a-98395c3bc3bb",
            "18989b1d-99b5-455b-841c-ab7c74e4ddfc",
            "4c5c32ff-bb9d-43b0-b5b4-2d72e54eaaa4",
            "a52bba46-e9e1-435f-b3d9-28daa648c0f6",
        }
        .Select(KnownFolder)
        .OfType<string>(),
    ];

    /// <summary>
    /// Where Windows says a known folder is, or null where it will not say.
    ///
    /// <para>Verification is switched off because the question is where the folder <em>is</em>, not
    /// whether it exists yet. A caller decides what to do about an absent directory by looking for
    /// the cache it wants, and without this flag a profile that has never had a low-integrity
    /// program run in it would answer identically to a platform that could not say at all.</para>
    ///
    /// <para><b>The identifiers are passed in as text rather than held in static fields.</b> These
    /// run from instance initialisers, and <see cref="Current"/> is a static field declared above
    /// any such field would be. Static initialisers run in textual order, so an identifier would
    /// still be <see cref="Guid.Empty"/> when the singleton constructs itself, the call would fail,
    /// and the one environment the application actually uses would report the folder as unknown
    /// while a freshly constructed one answered correctly.</para>
    /// </summary>
    private static string? KnownFolder(string id)
    {
        var folderId = new Guid(id);
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
