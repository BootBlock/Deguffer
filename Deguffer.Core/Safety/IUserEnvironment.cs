using System.Collections.Concurrent;
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

    /// <summary>Resolve an executable on <c>PATH</c>, or null if it is not installed.</summary>
    string? FindExecutable(string command);

    /// <summary>
    /// Read an environment variable, or null if it is unset.
    ///
    /// Exists because several tools relocate their cache through one — <c>PLAYWRIGHT_BROWSERS_PATH</c>
    /// is the first — and §5.2's "never assume a location" applies to the root just as much as to the
    /// children beneath it.
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
    /// Discard cached lookups. Called at the start of a planning pass so a toolchain installed
    /// while the app was open is picked up on the next preview.
    /// </summary>
    void Invalidate();
}

/// <inheritdoc />
public sealed partial class UserEnvironment : IUserEnvironment
{
    public static readonly UserEnvironment Current = new();

    private static readonly string[] PathExtensions =
        (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly string[] PathDirectories =
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Resolving a command probes the filesystem across every PATH directory, and both
    // IsPresentAsync and PlanAsync ask for the same tools. Memoised for the life of a planning
    // pass — including negative results, which is why Invalidate exists: without it, a toolchain
    // installed while the app is open would stay invisible for the rest of the session.
    private readonly ConcurrentDictionary<string, string?> _resolved = new(StringComparer.OrdinalIgnoreCase);

    public string UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string LocalAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string RoamingAppData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string? LocalLowAppData { get; } = ResolveLocalLow();

    public string TempPath { get; } = Path.GetTempPath();

    // Read once rather than through Invalidate: a process cannot change the account it runs as,
    // and relaunching elevated makes a new process with the same identity.
    public string? UserSecurityIdentifier { get; } = WindowsIdentity.GetCurrent().User?.Value;

    public void Invalidate() => _resolved.Clear();

    // Deliberately not memoised: a process environment read is a dictionary lookup, so caching it
    // would buy nothing and add a second thing for Invalidate to get wrong.
    public string? GetEnvironmentVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Environment.GetEnvironmentVariable(name);
    }

    /// <summary>
    /// Not memoised, for the reason <see cref="GetEnvironmentVariable"/> is not: the one caller
    /// memoises the answer it derives for the life of a planning pass, and a second cache here would
    /// be a second thing for <see cref="Invalidate"/> to get wrong.
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

        return _resolved.GetOrAdd(command, static name =>
        {
            foreach (var directory in PathDirectories)
            {
                foreach (var candidate in Candidates(directory, name))
                {
                    if (LongPath.FileExists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        });
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

    private static IEnumerable<string> Candidates(string directory, string command)
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

        foreach (var extension in PathExtensions)
        {
            yield return baseName + extension;
        }
    }
}
