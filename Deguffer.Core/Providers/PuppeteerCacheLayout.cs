using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Deguffer.Core.Providers;

/// <summary>
/// The names Puppeteer gives what it downloads, and nothing else. Its cache is two levels deep:
/// one folder per browser, and inside each one folder per build, named
/// <c>{platform}-{buildId}</c> by <c>@puppeteer/browsers</c>' <c>Cache.installationDir</c>.
///
/// <para><b>Each browser has its own build rule, and the rule is as strict as the builds it has
/// published.</b> Chrome for Testing numbers a build in four dotted parts (<c>127.0.6533.88</c>),
/// Chromium snapshots by a single revision (<c>1108766</c>), and Firefox by a Mozilla version with
/// the release channel in front (<c>stable_129.0</c>, <c>esr_128.2.0esr</c>). Puppeteer 19 and 20
/// kept Chromium revisions under <c>chrome</c> before Chrome for Testing existed, so that folder
/// takes either. A single rule wide enough for all of them would accept a Firefox version under
/// <c>chromedriver</c>, which Puppeteer never writes, and §5.2 fails in exactly that direction.</para>
///
/// <para>Case-insensitive, because Puppeteer reaches these folders through a path on a
/// case-insensitive file system: <c>Chrome\Win64-127.0.6533.88</c> is the build it launches.</para>
///
/// <para>Anchored with <c>\A</c> and <c>\z</c> rather than <c>^</c> and <c>$</c>, since <c>$</c>
/// also matches before a trailing newline.</para>
/// </summary>
internal static partial class PuppeteerCacheLayout
{
    /// <summary>
    /// The file in each browser folder mapping aliases such as <c>stable</c> to a build, and each
    /// build to its executable. Puppeteer resolves a launch through it, so it is never a target.
    /// </summary>
    public const string MetadataName = ".metadata";

    /// <summary>
    /// Every browser folder Puppeteer's <c>Browser</c> enumeration names, each with the test for a
    /// build inside it. A folder this does not name is Tier 4.
    /// </summary>
    private static readonly FrozenDictionary<string, BrowserFolder> Folders =
        new BrowserFolder[]
        {
            new("chrome", ChromeBuild()),
            new("chrome-headless-shell", ChromeForTestingBuild()),
            new("chromedriver", ChromeForTestingBuild()),
            new("chromium", ChromiumBuild()),
            new("firefox", FirefoxBuild()),
        }.ToFrozenDictionary(folder => folder.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every browser folder name, in Puppeteer's own spelling.</summary>
    public static IReadOnlyList<string> Browsers { get; } = [.. Folders.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Puppeteer's spelling of <paramref name="folder"/>, or null where it is not a browser folder.
    /// The spelling rather than the folder's own, so a build kept under <c>Chrome</c> stays kept
    /// when Puppeteer next creates the folder as <c>chrome</c>.
    /// </summary>
    public static string? Browser(string folder) =>
        Folders.TryGetValue(folder, out var known) ? known.Name : null;

    /// <summary>Whether <paramref name="name"/> is a build Puppeteer downloads into <paramref name="browser"/>'s folder.</summary>
    public static bool IsBuild(string browser, string name) =>
        Folders.TryGetValue(browser, out var known) && known.Build.IsMatch(name);

    /// <summary>
    /// The platform and the build of a name <see cref="IsBuild"/> accepted. Every platform and every
    /// build these rules accept is free of hyphens, so the first one is the separator.
    /// </summary>
    public static (string Platform, string BuildId) Split(string name)
    {
        var separator = name.IndexOf('-', StringComparison.Ordinal);

        return (name[..separator], name[(separator + 1)..]);
    }

    private sealed record BrowserFolder(string Name, Regex Build);

    private const string Platform = "(?:win32|win64|linux|linux_arm|mac|mac_arm)";

    [GeneratedRegex(
        @"\A" + Platform + @"-(?:[0-9]+|[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChromeBuild();

    [GeneratedRegex(
        @"\A" + Platform + @"-[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChromeForTestingBuild();

    [GeneratedRegex(
        @"\A" + Platform + @"-[0-9]+\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChromiumBuild();

    /// <summary>
    /// The channel prefix is optional because the first releases of <c>@puppeteer/browsers</c>
    /// installed Firefox Nightly under its bare version, <c>113.0a1</c>, and its own parser still
    /// reads such a name as Nightly.
    /// </summary>
    [GeneratedRegex(
        @"\A" + Platform + @"-(?:(?:stable|beta|devedition|esr|nightly)_)?[0-9]+\.[0-9]+(?:\.[0-9]+)*(?:a[0-9]+|b[0-9]+|esr)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirefoxBuild();
}
