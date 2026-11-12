using System.Collections.Concurrent;

namespace Deguffer.Core.Providers;

/// <summary>
/// Each Godot project's settings and each Godot 4 data folder's <see cref="GodotDataFolder"/>, read
/// once for a pass and handed to the three Godot rows.
///
/// <para>Shared because recognition reads <c>project.godot</c> for every candidate, and the search,
/// the plan and the declaration Explore reads each recognise the same folder, for each row (G4). The
/// data folder's reading is shared for the reason <see cref="CargoTargetSurvey"/> gives: both rows
/// divide one listing of it, so one entry is never named by one row and taken by the other. A reading
/// that raced another for the same folder is discarded, so every caller gets the one kept.</para>
/// </summary>
public sealed class GodotProjectSurvey
{
    private readonly ConcurrentDictionary<string, GodotProjectSettings?> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GodotDataFolder> _dataFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GodotCacheVerdict> _godot3ImportCaches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The settings of the project in <paramref name="project"/>, or null where they cannot be known.</summary>
    public GodotProjectSettings? Settings(string project) =>
        _settings.GetOrAdd(project, static folder => GodotProjectSettings.Read(folder));

    /// <summary>The entries of <paramref name="folder"/>, a recognised Godot 4 data folder, read once a pass.</summary>
    public GodotDataFolder DataFolder(string folder, CancellationToken ct) =>
        _dataFolders.GetOrAdd(folder, static (path, token) => GodotDataFolder.Of(path, token), ct);

    /// <summary>
    /// What the Godot 3 import folder at <paramref name="folder"/> holds, walked once a pass however
    /// many times the search, the plan and Explore's declaration recognise it.
    /// </summary>
    public GodotCacheVerdict Godot3ImportCache(string folder, CancellationToken ct) =>
        _godot3ImportCaches.GetOrAdd(folder, static (path, token) => GodotCacheContents.ImportCache(path, allowIgnoreFile: true, token), ct);

    /// <summary>
    /// Whether <paramref name="directory"/> is the data folder its project's settings name. The name
    /// carries the engine version, <c>godot</c> for Godot 4 and <c>import</c> for Godot 3, so a row
    /// searching for its own names never accepts the other version's folder.
    /// </summary>
    public bool IsDataFolder(string directory)
    {
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar);

        return Path.GetDirectoryName(trimmed) is { Length: > 0 } project
            && Settings(project) is { } settings
            && Path.GetFileName(trimmed).Equals(settings.DataFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drops every reading, so the next pass reads each project again.</summary>
    public void Invalidate()
    {
        _settings.Clear();
        _dataFolders.Clear();
        _godot3ImportCaches.Clear();
    }
}
