using System.Text;
using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What the IDE's own test says about a version's system directory.</summary>
public enum JetBrainsInstallStanding
{
    /// <summary>The install the directory's <c>.home</c> names is this version.</summary>
    Installed,

    /// <summary>The install the directory's <c>.home</c> names is now another version.</summary>
    Superseded,

    /// <summary>The folder the directory's <c>.home</c> names is not there at all.</summary>
    Uninstalled,

    /// <summary>
    /// Something the test needs could not be read: <c>.home</c> missing, refused or not a path, or the
    /// install or its <c>product-info.json</c> refused or unreadable.
    /// </summary>
    Unknown,
}

/// <summary>
/// One IntelliJ-platform IDE version's system directory, such as
/// <c>%LOCALAPPDATA%\Google\AndroidStudio2025.2.3</c>, and where the IDE's own test puts it.
/// </summary>
/// <param name="Path">The directory.</param>
/// <param name="Product">The name's leading part, such as <c>AndroidStudio</c> or <c>Rider</c>.</param>
/// <param name="Standing">What the installed test found.</param>
public sealed record JetBrainsSystemDirectory(
    string Path,
    string Product,
    JetBrainsInstallStanding Standing)
{
    /// <summary>
    /// The IDE's file naming the folder it was last started from, written into the system directory
    /// at every start (<c>ApplicationEx.LOCATOR_FILE_NAME</c>).
    /// </summary>
    public const string HomeFileName = ".home";

    /// <summary>The install's description, inside the folder <c>.home</c> names.</summary>
    public const string ProductInfoFileName = "product-info.json";

    /// <summary>
    /// The platform's directory name: a product, then a dotted version, and nothing after it
    /// (<c>SELECTOR_PATTERN</c> in <c>ConfigImportHelper.java</c>, whose matcher must match the whole
    /// name). A name with a suffix after the version, such as <c>Rider2024.3-backup</c>, is a
    /// "flavour" directory the platform itself skips, and fails this pattern too.
    /// </summary>
    private static readonly Regex Selector = new(@"^(\D+)(\d+(?:\.\d+)*)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Far beyond an install path, which is all <c>.home</c> holds. See <see cref="BoundedFile"/>.
    /// </summary>
    private const int HomeFileMaximumBytes = 32 * 1024;

    /// <summary>
    /// Far beyond <c>product-info.json</c>, a description of the install's launchers and layout of a
    /// few kilobytes. See <see cref="BoundedFile"/>.
    /// </summary>
    private const int ProductInfoMaximumBytes = 1024 * 1024;

    /// <summary>
    /// The product a directory's name gives it, or null where the name is not the platform's, or its
    /// product is not <paramref name="product"/> where one is required. Reads only the name.
    /// </summary>
    public static string? ProductOf(string path, string? product) =>
        Selector.Match(System.IO.Path.GetFileName(path)) is { Success: true } match
        && (product is null || match.Groups[1].Value.Equals(product, StringComparison.OrdinalIgnoreCase))
            ? match.Groups[1].Value
            : null;

    /// <summary>
    /// The directory read as a version's system directory of <paramref name="product"/>, the name
    /// <see cref="ProductOf"/> gave it, with its <c>.home</c> and the install that names. Never called
    /// for a link: everything it reads would be read through it.
    /// </summary>
    public static JetBrainsSystemDirectory Read(string path, string product) =>
        new(path, product, StandingOf(path));

    /// <summary>
    /// The installed test of <c>OldDirectoryCleaner.java</c>, with one change: every answer it could
    /// not read is <see cref="JetBrainsInstallStanding.Unknown"/>, where the IDE reads a missing or
    /// unreadable <c>.home</c> as "not installed". That reading only decides which rows the IDE's own
    /// dialog ticks; here it would decide a deletion, and a file Windows refused is no evidence.
    /// A missing <c>product-info.json</c> in an install that is there counts as installed, as it does
    /// in the IDE, which allows for a self-built install.
    /// </summary>
    private static JetBrainsInstallStanding StandingOf(string path)
    {
        if (ReadHome(System.IO.Path.Combine(path, HomeFileName)) is not { } home)
        {
            return JetBrainsInstallStanding.Unknown;
        }

        switch (LongPath.ProbeDirectory(home))
        {
            case PathPresence.Absent:
                return JetBrainsInstallStanding.Uninstalled;
            case PathPresence.Refused:
                return JetBrainsInstallStanding.Unknown;
        }

        var productInfo = System.IO.Path.Combine(home, ProductInfoFileName);

        switch (LongPath.ProbeFile(productInfo))
        {
            case PathPresence.Absent:
                return JetBrainsInstallStanding.Installed;
            case PathPresence.Refused:
                return JetBrainsInstallStanding.Unknown;
        }

        using var document = BoundedJsonFile.Read(productInfo, ProductInfoMaximumBytes);

        if (document is null || BoundedJsonFile.StringProperty(document.RootElement, "dataDirectoryName") is not { } dataDirectory)
        {
            return JetBrainsInstallStanding.Unknown;
        }

        // Compared ignoring case where the IDE compares exactly: Windows names are not case-sensitive,
        // and calling a version installed is the direction that keeps it.
        return dataDirectory.Equals(System.IO.Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
            ? JetBrainsInstallStanding.Installed
            : JetBrainsInstallStanding.Superseded;
    }

    /// <summary>
    /// The install folder <c>.home</c> names, or null where the file is missing, refused, not text,
    /// or not a full path. The IDE writes the path alone, with no line ending, and reads it the same
    /// way, so nothing is trimmed.
    /// </summary>
    private static string? ReadHome(string file)
    {
        if (BoundedFile.Read(file, HomeFileMaximumBytes) is not { } content)
        {
            return null;
        }

        try
        {
            var home = BoundedFile.StrictUtf8.GetString(content.Span);

            return home.Length > 0
                && home.AsSpan().IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0
                && System.IO.Path.IsPathFullyQualified(home)
                    ? home
                    : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
