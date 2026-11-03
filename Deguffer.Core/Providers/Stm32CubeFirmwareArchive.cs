using System.IO.Compression;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether a zip in an STM32Cube repository is the download of a firmware package unpacked beside it.
///
/// <para><b>Two facts, each from a different source, must agree.</b> The updater names the download of
/// <c>STM32Cube_FW_F4_V1.27.0</c> as <c>stm32cube_fw_f4_v1270.zip</c>: the folder's name in lower case
/// with its dots removed, as STM32CubeIDE's own log records and as ST's staff tell users to rename a
/// download they put in the repository by hand. And the zip, read from its directory without unpacking
/// it, holds that one folder and nothing beside it. The name is never parsed into a version, because
/// <c>v1115</c> reads as 1.11.5 and as 1.1.15, so a folder is mapped to the name and the zip is asked
/// what it holds.</para>
/// </summary>
internal static class Stm32CubeFirmwareArchive
{
    /// <summary>What every STM32Cube MCU package's folder is named with.</summary>
    public const string PackagePrefix = "STM32Cube_FW_";

    /// <summary>
    /// The file in a package's folder that says which package and version it is. ST: the version "is
    /// contained in" it, not in the zip's name. A folder without it is not a package that was unpacked.
    /// </summary>
    public const string PackageDescription = "package.xml";

    private const string ArchiveExtension = ".zip";

    /// <summary>Whether a folder in a repository is named as an STM32Cube MCU package.</summary>
    public static bool IsPackageName(string name) =>
        name.Length > PackagePrefix.Length && name.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a file in a repository is named as the updater names a package's download.</summary>
    public static bool IsArchiveName(string name) =>
        name.Length > PackagePrefix.Length + ArchiveExtension.Length
        && name.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(ArchiveExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The name the updater gives the download of <paramref name="package"/>.</summary>
    public static string ArchiveName(string package) =>
        package.Replace(".", string.Empty, StringComparison.Ordinal).ToLowerInvariant() + ArchiveExtension;

    /// <summary>What <paramref name="archive"/> was found to hold, against <paramref name="package"/>.</summary>
    public static ArchiveContents Contents(string archive, string package)
    {
        try
        {
            using var stream = new FileStream(
                LongPath.Extended(archive), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            // The directory at the end of the zip is read, and nothing is unpacked.
            var any = false;

            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var separator = name.IndexOf('/', StringComparison.Ordinal);

                if (separator <= 0 || !name[..separator].Equals(package, StringComparison.OrdinalIgnoreCase))
                {
                    return ArchiveContents.SomethingElse;
                }

                any = true;
            }

            return any ? ArchiveContents.OnlyThePackage : ArchiveContents.SomethingElse;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Still being downloaded, held open, refused, or not a zip: in each case nothing shows that
            // it is the package's download.
            return ArchiveContents.Unread;
        }
    }
}

/// <summary>What a zip in a repository holds, as far as pairing it with a package is concerned.</summary>
internal enum ArchiveContents
{
    /// <summary>One folder, the package's, and nothing beside it.</summary>
    OnlyThePackage,

    /// <summary>Anything else, including nothing.</summary>
    SomethingElse,

    /// <summary>The zip could not be opened or its directory could not be read.</summary>
    Unread,
}
