namespace Deguffer.Core.Safety;

/// <summary>
/// §6.3: long path support is mandatory. Node and NuGet trees routinely exceed MAX_PATH, and
/// truncating there is the most likely source of a silent partial deletion.
///
/// The app's manifest opts in to <c>longPathAware</c>, but Core is also consumed by a test host
/// that has no such manifest, so every filesystem call in Core goes through the extended-length
/// prefix rather than relying on process-wide configuration.
/// </summary>
public static class LongPath
{
    private const string DevicePrefix = @"\\?\";
    private const string UncDevicePrefix = @"\\?\UNC\";

    /// <summary>
    /// Return <paramref name="path"/> in extended-length form. Requires a rooted, already
    /// normalised path — the Win32 device namespace does no normalisation of its own, so
    /// <c>.</c>, <c>..</c> and relative segments must be resolved before prefixing.
    /// </summary>
    public static string Extended(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        var full = Path.GetFullPath(path);

        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? UncDevicePrefix + full[2..]
            : DevicePrefix + full;
    }

    /// <summary>
    /// A path the user configured, in the form the rest of the code may rely on: fully qualified,
    /// fully resolved, and without a trailing separator. Null when the value is not a full path, or
    /// names something Windows will not accept as one.
    ///
    /// <para>Every caller of this is a provider whose root comes from an environment variable or a
    /// settings file, and two things go wrong when such a value is used as it arrived. A trailing
    /// separator makes <see cref="Path.GetFileName(string)"/> return nothing, so a provider that
    /// splits a root from its leaf declares a target that resolves back to the directory it also
    /// asserts must survive — and §5.6 then reports a correct run as a failure. A value ending in
    /// <c>..</c> is worse: <see cref="Extended"/> requires an already-normalised path, because the
    /// Win32 device namespace resolves nothing, so the deletion would land one directory above the
    /// one the plan named.</para>
    ///
    /// <para><see cref="Path.GetFullPath(string)"/> is safe here only because the value is checked
    /// to be fully qualified first: an unqualified one would resolve against Deguffer's own working
    /// directory, which is a directory nobody pointed at.</para>
    /// </summary>
    public static string? Configured(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed) || !Path.IsPathFullyQualified(trimmed))
        {
            return null;
        }

        try
        {
            // A volume root keeps its separator, which is correct: "C:\" is the directory, and a
            // caller that must refuse a whole volume refuses it by name rather than by shape.
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Characters Windows will not accept in a path, so there is nothing here to point at.
            return null;
        }
    }

    /// <summary>Strip the extended-length prefix, for display and comparison.</summary>
    public static string Display(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.StartsWith(UncDevicePrefix, StringComparison.Ordinal))
        {
            return @"\\" + path[UncDevicePrefix.Length..];
        }

        return path.StartsWith(DevicePrefix, StringComparison.Ordinal)
            ? path[DevicePrefix.Length..]
            : path;
    }

    /// <summary>Whether the directory exists, tolerating paths beyond MAX_PATH.</summary>
    public static bool DirectoryExists(string path) => Directory.Exists(Extended(path));

    /// <summary>Whether the file exists, tolerating paths beyond MAX_PATH.</summary>
    public static bool FileExists(string path) => File.Exists(Extended(path));

    /// <summary>
    /// Whether this path is a junction or symbolic link rather than a real directory.
    ///
    /// <see cref="DirectoryExists"/> answers true for a junction and says nothing about it, so a
    /// target reached by name rather than by <see cref="ChildDirectories.Under"/> needs this to
    /// uphold the same rule: a link points at a tree the caller never classified, and deleting
    /// through it leaves the tree the plan described.
    ///
    /// Every caller reads false as "proceed", so this fails closed. A path that is not there is
    /// genuinely not a reparse point. A path we were refused, could not read, or could not even
    /// parse is not an answer at all, and the only safe reading of "I cannot tell" on a predicate
    /// guarding a deletion is the one that stops it. The cost of being wrong that way is one
    /// unexplained decline.
    ///
    /// <see cref="DotNetIntermediateSignature"/> carried its own copy of this rule and now calls
    /// here instead. A safety predicate written twice is one that gets changed once.
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(Extended(path));

            return attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return true;
        }
    }
}
