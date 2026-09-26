namespace Deguffer.Core.Providers;

/// <summary>How a Chromium host's user-data folder tells its profiles from its other children.</summary>
public enum ChromiumProfileRule
{
    /// <summary>
    /// Chromium's own names: <c>Default</c>, and <c>Profile</c> followed by a number. What every
    /// browser and every Electron application writes.
    /// </summary>
    Named,

    /// <summary>
    /// A child directory that holds the host's identifying file itself. The Chromium Embedded
    /// Framework leaves the name of each profile, which it calls a cache path, to the application,
    /// so there is no name to match: Battle.net calls its one partition <c>common</c>. What the
    /// measured host does write is its settings file into each of them, so a profile is found by
    /// the same positive test that found the folder around it. A directory without that file is not
    /// looked inside, which is the safe direction to be wrong in.
    /// </summary>
    Marked,

    /// <summary>
    /// Chromium's own names, plus the <c>WV2Profile_&lt;name&gt;</c> directory WebView2 creates for
    /// each named profile a host application asks for. Microsoft documents only that each profile
    /// gets "a dedicated profile folder", so the prefix is observed rather than documented. The one
    /// measured host that uses the feature keeps all of its real cache and all of its sign-in state
    /// in there, beside an almost empty <c>Default</c>.
    /// </summary>
    WebView2,
}

/// <summary>
/// The file that marks a folder as a Chromium user-data folder, and how the profiles inside it are
/// found. A fact about which embedding of the engine wrote the folder, rather than about any one
/// application.
/// </summary>
/// <param name="IdentifyingFile">
/// The file the engine writes into the user-data folder it owns. It holds the settings that span
/// every profile and, on Windows, the DPAPI-wrapped key that decrypts the cookies and saved
/// passwords beside it, which is why <see cref="ChromiumCacheProvider"/> asserts it survived.
/// </param>
/// <param name="Profiles">How the profiles inside the folder are recognised.</param>
public sealed record ChromiumLayout(string IdentifyingFile, ChromiumProfileRule Profiles)
{
    /// <summary>A browser, or an application embedding the engine through Electron.</summary>
    public static readonly ChromiumLayout Browser = new("Local State", ChromiumProfileRule.Named);

    /// <summary>
    /// An application embedding the engine through the Chromium Embedded Framework, which names the
    /// same settings file <c>LocalPrefs.json</c>. Declared per host rather than walked for, because
    /// the file name is generic enough that finding it one level under an application-data root
    /// would say nothing about whose folder it was.
    /// </summary>
    public static readonly ChromiumLayout EmbeddedFramework = new("LocalPrefs.json", ChromiumProfileRule.Marked);

    /// <summary>
    /// An application embedding the engine through WebView2, whose folder is the
    /// <c>EBWebView</c> directory the runtime creates inside the folder the application chose. The
    /// same marker as a browser's, and profiles named by <see cref="ChromiumProfileRule.WebView2"/>.
    /// </summary>
    public static readonly ChromiumLayout WebView2 = new("Local State", ChromiumProfileRule.WebView2);
}
