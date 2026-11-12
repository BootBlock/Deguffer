using System.Security;
using Microsoft.Win32;

namespace Deguffer.Core.Safety;

/// <summary>
/// One account's registry hive, read and never written: where the programs that account runs keep
/// their settings.
///
/// <para><b>A hive rather than <c>HKEY_CURRENT_USER</c>, because the two are not always the same
/// account.</b> An elevated process run with another account's credentials, or under Windows'
/// administrator protection, sees that account's hive as <c>HKEY_CURRENT_USER</c>. A setting the
/// user signed in to Windows chose is then under <c>HKEY_USERS\&lt;their SID&gt;</c>, and the
/// one under <c>HKEY_CURRENT_USER</c> is somebody else's. See
/// <see cref="IUserEnvironment.UserHive"/> and <see cref="IUserEnvironment.RunningAccountHive"/>.</para>
///
/// <para>Every read answers null, or nothing, when the key, the value or the permission to read it is
/// missing, or the value is of another kind. Not memoised, so no answer can outlive
/// <see cref="IUserEnvironment.Invalidate"/>. A caller that needs one for the life of a planning pass
/// keeps what it derives from it.</para>
/// </summary>
public interface IUserHive
{
    /// <summary>
    /// A string value.
    ///
    /// <para>Exists because Steam records where it is installed and nothing else on disk does. The
    /// install directory is not under the profile, it moves with the user's game library, and §5.2's
    /// "never assume a location" forbids guessing at <c>%PROGRAMFILES(X86)%\Steam</c>.</para>
    ///
    /// <para><b>The user's hive, and deliberately so.</b> The machine-wide key is redirected under
    /// <c>WOW6432Node</c> for a 64-bit process, which is a second thing to get wrong for an answer
    /// about an install this account may never have run. Steam's own client writes the per-user value
    /// every time it starts, so a user with a Steam cache to reclaim has it.</para>
    /// </summary>
    /// <param name="keyPath">The key, relative to the hive's root.</param>
    /// <param name="valueName">The value to read.</param>
    string? ReadValue(string keyPath, string valueName);

    /// <summary>
    /// A <c>REG_DWORD</c> value.
    ///
    /// <para>Exists because WSL records whether a distribution runs in a virtual machine only as a bit
    /// of its <c>Flags</c> number, and a distribution without that bit has no virtual disk at all. Read
    /// as a string, the value would be null and the two kinds of distribution would look alike.</para>
    /// </summary>
    /// <param name="keyPath">The key, relative to the hive's root.</param>
    /// <param name="valueName">The value to read.</param>
    int? ReadNumber(string keyPath, string valueName);

    /// <summary>
    /// The names of the keys directly under a key.
    ///
    /// <para>Exists because Adobe files its media cache settings under a key whose name carries a
    /// version, such as <c>Common 13.0</c>, and the version moves between releases. Guessing at the
    /// names would miss a release nobody listed, and a location that is missed is a cache that is
    /// never found rather than a wrong deletion, so the names are read instead.</para>
    /// </summary>
    /// <param name="keyPath">The key, relative to the hive's root.</param>
    IReadOnlyList<string> ReadSubKeyNames(string keyPath);

    /// <summary>
    /// The names of the values directly under a key.
    ///
    /// <para>Exists because the Unity Editor names each of its preferences' values with a hash of the
    /// preference's name appended, as <c>GICacheFolder_h</c> and digits. The names are read rather
    /// than the hash computed, so a value is found by what Unity wrote, not by a formula nobody
    /// here has seen Unity apply.</para>
    /// </summary>
    /// <param name="keyPath">The key, relative to the hive's root.</param>
    IReadOnlyList<string> ReadValueNames(string keyPath);

    /// <summary>
    /// A <c>REG_BINARY</c> value.
    ///
    /// <para>Exists because the Unity Editor stores a text preference, such as where it keeps its GI
    /// cache, as bytes rather than as a string, so <see cref="ReadValue"/> would read it as
    /// absent.</para>
    /// </summary>
    /// <param name="keyPath">The key, relative to the hive's root.</param>
    /// <param name="valueName">The value to read.</param>
    byte[]? ReadBytes(string keyPath, string valueName);
}

/// <inheritdoc />
public sealed class UserHive : IUserHive
{
    /// <summary><c>HKEY_CURRENT_USER</c>: the hive of the account this process runs as.</summary>
    public static readonly UserHive CurrentUser = new(Registry.CurrentUser, prefix: null);

    /// <summary>
    /// A hive that answers nothing, for a process that cannot say whose settings it would be reading.
    /// A setting that is not read leaves a provider at its default location, or finding nothing,
    /// which is the direction §5.2 fails in.
    /// </summary>
    public static readonly UserHive Unread = new(root: null, prefix: null);

    private readonly RegistryKey? _root;
    private readonly string? _prefix;

    private UserHive(RegistryKey? root, string? prefix)
    {
        _root = root;
        _prefix = prefix;
    }

    /// <summary>
    /// <c>HKEY_USERS\&lt;<paramref name="securityIdentifier"/>&gt;</c>: the hive of an account signed
    /// in to this machine, which Windows keeps loaded while that account is signed in. An administrator
    /// may read it, and any other account is refused, which every read answers as nothing.
    /// </summary>
    public static UserHive Of(string securityIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityIdentifier);

        return new UserHive(Registry.Users, securityIdentifier);
    }

    public string? ReadValue(string keyPath, string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        // Anything that is not a string is not an answer to this question. A REG_DWORD where a path
        // was expected would otherwise arrive as its decimal digits and be treated as one.
        return Read(keyPath, key => key.GetValue(valueName) as string);
    }

    public int? ReadNumber(string keyPath, string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        // A REG_DWORD arrives as an int and nothing else does, so a string holding digits is not
        // read as a number.
        return Read(keyPath, key => key.GetValue(valueName) is int number ? number : (int?)null);
    }

    public IReadOnlyList<string> ReadSubKeyNames(string keyPath) =>
        Read<IReadOnlyList<string>>(keyPath, key => key.GetSubKeyNames()) ?? [];

    public IReadOnlyList<string> ReadValueNames(string keyPath) =>
        Read<IReadOnlyList<string>>(keyPath, key => key.GetValueNames()) ?? [];

    public byte[]? ReadBytes(string keyPath, string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);

        // A REG_BINARY arrives as a byte array and nothing else does.
        return Read(keyPath, key => key.GetValue(valueName) as byte[]);
    }

    private T? Read<T>(string keyPath, Func<RegistryKey, T?> read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);

        if (_root is null)
        {
            return default;
        }

        try
        {
            using var key = _root.OpenSubKey(_prefix is null ? keyPath : $@"{_prefix}\{keyPath}");

            return key is null ? default : read(key);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // A hive this account may not read, and a key already marked for deletion. Both are
            // ordinary on a long-lived machine, and both mean the same thing here: nothing said
            // where the tool is.
            return default;
        }
    }
}
