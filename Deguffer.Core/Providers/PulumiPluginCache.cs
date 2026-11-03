using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One reading of Pulumi's plugin cache: every entry in the folder, and which of them are installed
/// plugins this recognises.
///
/// <para><b>From one listing, so the parts agree.</b> Whether a plugin has finished installing is a
/// file beside its folder, and so is when it was last used. Both are read from the same listing as the
/// folders, so a plugin is never judged on one reading of the folder and offered on another. A folder
/// Windows will not list in full gives no reading at all, for the reason <see cref="FolderEntries"/>
/// gives.</para>
/// </summary>
internal sealed class PulumiPluginCache
{
    private readonly Dictionary<string, FileSystemInfo> _byName;

    private PulumiPluginCache(string folder, IReadOnlyList<FileSystemInfo> entries)
    {
        Folder = folder;
        Entries = entries;
        _byName = entries.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);

        var recognised = entries
            .Where(entry => entry is DirectoryInfo && !IsLink(entry))
            .Select(entry => PulumiPlugin.Recognise(LongPath.Display(entry.FullName)))
            .OfType<PulumiPlugin>()
            .ToList();

        Installed = [.. recognised.Where(plugin => !HasFile(plugin.Sidecar(PulumiPlugin.PartialSuffix)))];
        Installing = [.. recognised.Except(Installed)];
    }

    /// <summary>The plugin cache, in display form.</summary>
    public string Folder { get; }

    /// <summary>Everything directly inside the cache, in long form, as Windows listed it.</summary>
    public IReadOnlyList<FileSystemInfo> Entries { get; }

    /// <summary>The plugins Pulumi has finished installing, each a folder of its own and not a link.</summary>
    public IReadOnlyList<PulumiPlugin> Installed { get; }

    /// <summary>
    /// The plugins whose install has not finished: Pulumi is installing one now, or an install stopped
    /// part-way. Pulumi does not list one, and installs it again when it next needs it.
    /// </summary>
    public IReadOnlyList<PulumiPlugin> Installing { get; }

    /// <summary>The cache as it stands now, or null where Windows would not list all of it.</summary>
    public static PulumiPluginCache? Read(string folder) =>
        FolderEntries.Of(folder) is { } entries ? new PulumiPluginCache(LongPath.Display(folder), entries) : null;

    /// <summary>Whether the listing holds a file, not a link, at <paramref name="path"/>.</summary>
    public bool HasFile(string path) =>
        _byName.TryGetValue(Path.GetFileName(path), out var entry) && entry is FileInfo && !IsLink(entry);

    /// <summary>The sidecar files of <paramref name="plugin"/> that are there, in display form.</summary>
    public IReadOnlyList<string> SidecarsOf(PulumiPlugin plugin) =>
        [.. PulumiPlugin.SidecarSuffixes.Select(plugin.Sidecar).Where(HasFile)];

    /// <summary>
    /// When Pulumi last started <paramref name="plugin"/>: the time of its <c>.lastused</c> file, else when
    /// its folder last changed, which is when it was installed. Pulumi falls back the same way, though to
    /// the folder's access time, which Windows does not keep up to date.
    /// </summary>
    public DateTime LastUsedUtc(PulumiPlugin plugin) =>
        _byName.TryGetValue(Path.GetFileName(plugin.Sidecar(PulumiPlugin.LastUsedSuffix)), out var marker) && marker is FileInfo
            ? marker.LastWriteTimeUtc
            : _byName[Path.GetFileName(plugin.Folder)].LastWriteTimeUtc;

    /// <summary>
    /// The first folder beside <paramref name="plugin"/> that <c>pulumi plugin rm</c> for it would also
    /// remove, or null where it would remove that plugin alone.
    /// </summary>
    public string? AlsoRemovedWith(PulumiPlugin plugin) =>
        Entries
            .Where(entry => entry is DirectoryInfo)
            .Select(entry => entry.Name)
            .FirstOrDefault(plugin.CouldAlsoRemove);

    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
}
