using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The disk cache folders After Effects' own preferences name, and what became of reading them.
/// </summary>
/// <param name="Folders">Each folder chosen for the disk cache, as a full path, with no repeats.</param>
/// <param name="Versions">Every version's preferences folder found.</param>
/// <param name="Unset">
/// The versions whose preferences were read and name no folder Deguffer can use. After Effects then
/// uses a default Adobe does not publish, so nothing is looked for on its behalf.
/// </param>
/// <param name="Unread">
/// The preferences files that were there and not read, and the folders that would not be listed. A
/// folder named only in one of these is unknown.
/// </param>
public sealed record AfterEffectsCacheSettings(
    IReadOnlyList<string> Folders,
    IReadOnlyList<string> Versions,
    IReadOnlyList<string> Unset,
    IReadOnlyList<string> Unread)
{
    /// <summary>
    /// Whether After Effects may have kept preferences for this user. A folder that would not be
    /// listed counts, since "not used" is a claim nothing established.
    /// </summary>
    public bool Found => Versions.Count > 0 || Unread.Count > 0;

    /// <summary>Whether every preferences file and folder found was read.</summary>
    public bool IsComplete => Unread.Count == 0;
}

/// <summary>
/// Reads the disk cache folder each version of After Effects was told to use, from the preferences
/// it keeps under <c>%APPDATA%\Adobe\After Effects\&lt;version&gt;</c>.
///
/// <para><b>Every text file in a version's folder is read, not one named file.</b> The name is
/// translated: an English install writes <c>Adobe After Effects 22.6 Prefs.txt</c>, a Russian one
/// <c>Adobe After Effects 22.6 Установки.txt</c>, and a Spanish one puts <c>Preferencias</c> first.
/// The section is what identifies the file, and the other files in the folder do not have it.</para>
///
/// <para>The files also hold settings that are none of Deguffer's business. Only the folder values
/// leave this type, and nothing read here is logged or shown.</para>
/// </summary>
public static class AfterEffectsCacheSettingsReader
{
    /// <summary>Far past any preferences file After Effects writes, which run to about 100 KB.</summary>
    private const int MaximumBytes = 4 * 1024 * 1024;

    private const string PreferencesExtension = ".txt";

    public static AfterEffectsCacheSettings Read(string roamingAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingAppData);

        var root = AfterEffectsDiskCacheLayout.PreferencesRoot(roamingAppData);
        var folders = new List<string>();
        var versions = new List<string>();
        var unset = new List<string>();
        var unread = new List<string>();
        var children = ChildDirectories.Under(root);

        if (children.Unreadable)
        {
            return new AfterEffectsCacheSettings(folders, versions, unset, [root]);
        }

        // A version folder that is a link is read through, as After Effects reading its own
        // preferences would. Only files are read here, and nothing reached through it is a target.
        foreach (var version in children.Directories.Concat(children.Links))
        {
            var path = LongPath.Display(version.FullName);
            versions.Add(path);

            var unreadBefore = unread.Count;
            var named = FoldersIn(path, unread);

            // A version with a file that could not be read may name a folder in it, so it is unknown
            // rather than unset.
            if (named.Count == 0 && unread.Count == unreadBefore)
            {
                unset.Add(path);
            }

            foreach (var folder in named.Where(folder => !folders.Contains(folder, StringComparer.OrdinalIgnoreCase)))
            {
                folders.Add(folder);
            }
        }

        return new AfterEffectsCacheSettings(folders, versions, unset, unread);
    }

    /// <summary>
    /// Every folder the preferences in <paramref name="version"/> name for the disk cache. A file
    /// that could not be read, or the folder where it would not be listed, is added to
    /// <paramref name="unread"/>.
    /// </summary>
    private static List<string> FoldersIn(string version, List<string> unread)
    {
        var found = new List<string>();

        if (FolderEntries.Of(version) is not { } entries)
        {
            unread.Add(version);
            return found;
        }

        foreach (var file in entries.OfType<FileInfo>()
                     .Where(entry => entry.Extension.Equals(PreferencesExtension, StringComparison.OrdinalIgnoreCase)))
        {
            var path = LongPath.Display(file.FullName);

            if (BoundedFile.Read(path, MaximumBytes) is not { } content)
            {
                unread.Add(path);
                continue;
            }

            // The file is ASCII, with every other byte written out in hexadecimal, so Latin-1 hands
            // each byte over unchanged and leaves the decoding to the format's own rules.
            var values = AfterEffectsPreferenceText.Values(
                Encoding.Latin1.GetString(content.Span),
                AfterEffectsDiskCacheLayout.PreferencesSection,
                AfterEffectsDiskCacheLayout.IsFolderKey);

            // Unaliased, because After Effects records the temporary folder as the environment gives
            // it, which on a profile with a long folder name is the 8.3 short form.
            found.AddRange(values.Found
                .Select(value => LongPath.Unaliased(LongPath.Configured(value.Value)))
                .OfType<string>());

            if (values.HasUnreadable)
            {
                unread.Add(path);
            }
        }

        return found;
    }
}
