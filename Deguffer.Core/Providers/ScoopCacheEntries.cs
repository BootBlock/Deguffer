using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What sits directly inside Scoop's download cache, split into what Scoop itself put there and
/// everything else (§5.2).
///
/// <para><b>Why the split decides whether anything is offered.</b> <c>scoop cache rm *</c> lists the
/// folder with <c>Get-ChildItem</c> and passes every entry to <c>Remove-Item</c> without looking at
/// its name (<c>libexec/scoop-cache.ps1</c> at <c>e6aa3b366bdee8ed138c1e0f7b85192ebdd35d0f</c>). A
/// file of anyone's goes with the downloads, and an empty folder too. So the command is offered only
/// where every entry is one Scoop wrote, and these are the names it writes:</para>
///
/// <list type="bullet">
/// <item>A download, <c>&lt;app&gt;#&lt;version&gt;#&lt;url&gt;</c> from <c>cache_path</c> in
/// <c>lib/core.ps1</c>. The older form replaces each run of characters outside <c>[\w.-]</c> in the URL
/// with <c>_</c>; the newer one uses the first seven hex digits of the URL's SHA-256 and the URL's
/// extension.</item>
/// <item>Its <c>.download</c> partial, which the built-in downloader renames into place, and aria2's
/// <c>.aria2</c> control file (<c>lib/download.ps1</c>). Both are the download's name with a suffix,
/// so the forms above already cover them.</item>
/// <item>aria2's input list, <c>&lt;app&gt;.txt</c>, which Scoop deletes when aria2 is done, and
/// which survives only an interrupted download. Its name alone is any text file's, so it is Scoop's
/// only where a download of the same app sits beside it; <c>cacheremove</c> pairs the two the same
/// way.</item>
/// </list>
///
/// <para>A link, a folder, and a file of any other name are unrecognised: Tier 4, and the reason the
/// command is not offered.</para>
/// </summary>
internal sealed partial record ScoopCacheEntries(IReadOnlyList<string> Recognised, IReadOnlyList<string> Unrecognised)
{
    private const string InputListExtension = ".txt";

    /// <summary>How many unrecognised entries a sentence names before it summarises the rest.</summary>
    private const int EntriesNamed = 3;

    /// <summary>
    /// The entries of <paramref name="cache"/>, by full path in display form, or null where the
    /// folder would not be listed to the end. A partial listing is discarded, because what decides
    /// the offer is that <em>nothing</em> unrecognised is there.
    /// </summary>
    public static ScoopCacheEntries? Of(string cache, CancellationToken ct)
    {
        if (FolderEntries.Of(cache) is not { } entries)
        {
            return null;
        }

        var downloads = new List<FileSystemInfo>();
        var others = new List<FileSystemInfo>();

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            (IsFile(entry) && IsDownloadName(entry.Name) ? downloads : others).Add(entry);
        }

        var apps = downloads.Select(download => AppOf(download.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lists = others.Where(entry => IsFile(entry) && IsInputListOf(entry.Name, apps)).ToList();

        return new ScoopCacheEntries(
            [.. downloads.Concat(lists).Select(DisplayPath)],
            [.. others.Except(lists).Select(DisplayPath)]);
    }

    /// <summary>
    /// Whether <paramref name="name"/> is one of the names a download, its partial or its aria2
    /// control file is given. Explore asks this of one entry at a time and cannot see the siblings an
    /// input list is recognised by, so it refuses those.
    /// </summary>
    public static bool IsDownloadName(string name) => DownloadName().IsMatch(name);

    /// <summary>
    /// The unrecognised entries as a sentence names them, such as <c>2 entries that Scoop did not
    /// make: 'notes.txt', 'installers'</c>.
    /// </summary>
    public string DescribeUnrecognised()
    {
        var named = string.Join(", ", Unrecognised.Take(EntriesNamed).Select(entry => $"'{Path.GetFileName(entry)}'"));
        var rest = Unrecognised.Count > EntriesNamed ? $" and {Unrecognised.Count - EntriesNamed} more" : string.Empty;
        var count = Unrecognised.Count == 1 ? "1 entry" : $"{Unrecognised.Count} entries";

        return $"{count} that Scoop did not make: {named}{rest}";
    }

    private static bool IsFile(FileSystemInfo entry) =>
        entry is FileInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static string AppOf(string download) => download[..download.IndexOf('#', StringComparison.Ordinal)];

    private static bool IsInputListOf(string name, IReadOnlySet<string> apps) =>
        name.EndsWith(InputListExtension, StringComparison.OrdinalIgnoreCase)
        && apps.Contains(name[..^InputListExtension.Length]);

    private static string DisplayPath(FileSystemInfo entry) => LongPath.Display(entry.FullName);

    /// <summary>
    /// <c>app#version#</c> and then either the older underscored URL, whose characters are all
    /// <c>[\w.-]</c>, or seven lowercase hex digits and an extension, which can carry any character
    /// a file name may.
    /// </summary>
    [GeneratedRegex(@"\A[^#]+#[^#]+#(?:[\w.\-]+|[0-9a-f]{7}\.[^#]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadName();
}
