namespace Deguffer.Core.Providers;

/// <summary>
/// Where After Effects keeps its disk cache on Windows, and the one folder in it that is the cache.
///
/// <para><b>The folder is the user's choice, so it is read, never assumed.</b> Adobe documents the
/// setting only as a Choose Folder button under Media &amp; Disk Cache, and publishes no default
/// path. After Effects records the choice in its machine-specific preferences file as
/// <c>"Folder 7"</c> in the <c>["Disk Cache Controls"]</c> section: preferences files from versions 13
/// and 22 carry it under that name, and scripts for version 12 onwards read it through
/// <c>app.preferences</c> by it. Version 11 called it <c>"Folder 6"</c>. A Windows install of version 13 recorded the temporary
/// folder there, which is why the cache is so often in <c>%TEMP%</c>.</para>
///
/// <para><b>After Effects writes below the chosen folder, never into it.</b> The cache itself is
/// <c>&lt;folder&gt;\Adobe\After Effects\&lt;version&gt;\Disk Cache - &lt;computer&gt;.noindex</c>: the
/// shape a script that empties it on Windows builds from <c>app.version</c> and
/// <c>system.machineName</c>, and the shape users report on macOS. Every version keeps its own, and
/// Empty Disk Cache in one version leaves the others' in place, which is where the space goes.</para>
///
/// <para><b>§5.2.</b> Only a folder named for this computer is a target. The chosen folder is often
/// one the user keeps other things in, and the folders above the cache, and anything beside it, are
/// never taken. A cache named for another computer is left alone: a disk cache on a shared drive can
/// be in use by an After Effects this computer's process table cannot see.</para>
/// </summary>
public static class AfterEffectsDiskCacheLayout
{
    /// <summary>The section of the preferences file that holds the disk cache settings.</summary>
    public const string PreferencesSection = "Disk Cache Controls";

    /// <summary>What every key naming the cache folder begins with, before its number.</summary>
    private const string FolderKeyPrefix = "Folder ";

    private const string CacheNamePrefix = "Disk Cache - ";
    private const string CacheNameSuffix = ".noindex";

    /// <summary>
    /// The program, and the command-line renderer, which uses the same cache, as the process table
    /// names them.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["AfterFX", "aerender"];

    /// <summary>
    /// Whether a key in <see cref="PreferencesSection"/> names the cache folder. The number after
    /// <c>Folder</c> has changed between versions, so any number is accepted, and each value is looked
    /// in: a folder that holds no cache offers nothing.
    /// </summary>
    public static bool IsFolderKey(string key) =>
        key.StartsWith(FolderKeyPrefix, StringComparison.Ordinal)
        && key.Length > FolderKeyPrefix.Length
        && !key.AsSpan(FolderKeyPrefix.Length).ContainsAnyExceptInRange('0', '9');

    /// <summary>The folder holding one folder of preferences per version of After Effects.</summary>
    public static string PreferencesRoot(string roamingAppData) =>
        Path.Combine(roamingAppData, "Adobe", "After Effects");

    /// <summary>The folder After Effects makes below a chosen cache folder, holding one folder per version.</summary>
    public static string VersionsUnder(string chosenFolder) =>
        Path.Combine(chosenFolder, "Adobe", "After Effects");

    /// <summary>The name of the cache this computer's After Effects writes.</summary>
    public static string CacheName(string machineName) => CacheNamePrefix + machineName + CacheNameSuffix;

    /// <summary>Whether a name is any computer's disk cache, this one's or not.</summary>
    public static bool IsAnyCacheName(string name) =>
        name.StartsWith(CacheNamePrefix, StringComparison.OrdinalIgnoreCase)
        && name.EndsWith(CacheNameSuffix, StringComparison.OrdinalIgnoreCase);
}
