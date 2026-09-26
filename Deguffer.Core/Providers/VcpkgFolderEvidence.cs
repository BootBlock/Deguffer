using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether a folder a vcpkg variable names holds what vcpkg puts there, read from its contents.
///
/// <para><b>Asked only of a folder a variable moved.</b> A cache at the place vcpkg itself chooses is
/// vcpkg's by where it is. A folder a variable names is vcpkg's only if vcpkg has written to it,
/// and it is removed whole, so a variable naming the folder somebody keeps their projects in would
/// otherwise take the projects. Where the contents do not say vcpkg, the folder is left alone and
/// the user is told why.</para>
///
/// <para>Researched against vcpkg-tool's source rather than measured, for the reason the provider
/// was.</para>
/// </summary>
internal static partial class VcpkgFolderEvidence
{
    /// <summary>
    /// A binary cache shard: the first two characters of the package's ABI hash, which vcpkg writes
    /// in lower-case hexadecimal (<c>files_archive_parent_path</c>).
    /// </summary>
    [GeneratedRegex(@"\A[0-9a-f]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ShardName();

    /// <summary>
    /// One cached package: the whole 64-character ABI hash and <c>.zip</c>, whatever made the
    /// archive, or the same with the process number vcpkg appends while it copies one in.
    /// </summary>
    [GeneratedRegex(@"\A[0-9a-f]{64}\.zip(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveName();

    /// <summary>
    /// A tool vcpkg unpacked into its downloads folder: <c>&lt;tool&gt;-&lt;version&gt;-&lt;os&gt;</c>,
    /// as <c>cmake-3.30.1-windows</c> or <c>7zip-24.09-windows</c>.
    /// </summary>
    [GeneratedRegex(
        @"\A.+-[0-9][^-]*-(?:windows|linux|osx|freebsd|openbsd|solaris)\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ToolName();

    /// <summary>
    /// Why <paramref name="folder"/> is not shown to be a vcpkg binary cache, as the end of a
    /// sentence, or null where it is.
    ///
    /// <para>Every top-level entry is a shard folder and at least one shard holds a cached package.
    /// vcpkg writes nothing else there, so a single file or folder of another name means somebody
    /// else uses the folder too. An empty folder holds nothing to remove.</para>
    /// </summary>
    public static string? WhyNotABinaryCache(string folder)
    {
        var found = false;

        foreach (var entry in Entries(folder))
        {
            if (entry is not DirectoryInfo shard || !ShardName().IsMatch(shard.Name))
            {
                return $"'{entry.Name}' is in it, and vcpkg keeps nothing in its binary cache but folders named "
                    + "by two hexadecimal digits.";
            }

            found = found || HoldsPackage(shard);
        }

        return found ? null : "nothing in it is a package vcpkg cached.";
    }

    /// <summary>
    /// Why <paramref name="folder"/> is not shown to be a vcpkg downloads folder, as the end of a
    /// sentence, or null where it is.
    ///
    /// <para>vcpkg writes no marker there, and the source archives are named by each port, so the
    /// only thing that says vcpkg is the tools it unpacks under <c>tools</c>, each named for the tool,
    /// its version and the system. A downloads folder that has never needed a tool is left alone,
    /// which is the direction §5.2 requires the unknown case to fail in.</para>
    /// </summary>
    public static string? WhyNotDownloads(string folder)
    {
        var tools = Path.Combine(folder, "tools");

        return Entries(tools).OfType<DirectoryInfo>().Any(tool => ToolName().IsMatch(tool.Name))
            ? null
            : "it holds no 'tools' folder of the tools vcpkg unpacks, the one thing vcpkg always "
                + "writes there that says the folder is its own.";
    }

    /// <summary>
    /// The folder's own entries, or none where Windows will not list it. A folder that cannot be
    /// read shows nothing, and nothing is not evidence.
    /// </summary>
    private static IReadOnlyList<FileSystemInfo> Entries(string folder)
    {
        try
        {
            return [.. new DirectoryInfo(LongPath.Extended(folder)).EnumerateFileSystemInfos()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether <paramref name="shard"/> holds a package filed under its own two digits, looked for
    /// only until one is found.
    /// </summary>
    private static bool HoldsPackage(DirectoryInfo shard)
    {
        try
        {
            return shard.EnumerateFiles().Any(file => ArchiveName().IsMatch(file.Name)
                && file.Name.StartsWith(shard.Name, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A shard Windows will not list shows no package, and no package is not evidence.
            return false;
        }
    }
}
