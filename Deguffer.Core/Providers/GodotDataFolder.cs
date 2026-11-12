using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What a Godot 4 project's data folder holds, divided between the two rows that offer parts of it:
/// the import cache (<see cref="GodotImportCacheProvider"/>) and the shader cache
/// (<see cref="GodotShaderCacheProvider"/>).
///
/// <para><b>Why the data folder is a tool root rather than a disposable directory.</b> Godot's
/// documentation calls <c>.godot</c> "project cache data", but the editor also keeps
/// <c>export_credentials.cfg</c> there, holding the script encryption key and every export option
/// marked secret, such as a keystore password
/// (<see href="https://github.com/godotengine/godot/blob/master/editor/export/editor_export.cpp">editor_export.cpp</see>).
/// Beside it are the editor's per-project state in <c>editor</c>, the C# build output in
/// <c>mono</c>, and the caches of script classes, unique ids and scene groups, which the editor
/// rewrites but a game run without them may fail to start. So §5.2 applies: two recognised children
/// only, and everything else is left standing and named.</para>
///
/// <para><b>Two rows, because the costs differ.</b> Removing <c>imported</c> makes the editor import
/// every asset again on the next open, which on a large project takes minutes. Removing
/// <c>shader_cache</c> costs one slower start while the shaders compile. Each row names everything but
/// the two caches as left alone, and names its own cache only when it declines it, so one cache is
/// never offered by one row and asserted by the other.</para>
/// </summary>
public sealed class GodotDataFolder
{
    /// <summary>The import cache's folder inside the data folder.</summary>
    public const string ImportCacheName = "imported";

    /// <summary>The shader cache's folder inside the data folder.</summary>
    public const string ShaderCacheName = "shader_cache";

    /// <summary>
    /// What makes a folder a Godot 4 project's data folder, for both rows: a <c>project.godot</c> beside
    /// it, whose settings say the project is Godot 4's and name this folder as its data folder
    /// (<see cref="GodotProjectSettings"/>). The name alone is not evidence, and <c>godot</c> is the
    /// name many repositories give the folder holding their Godot project.
    ///
    /// <para><b>Git is asked for a second opinion.</b> The engine's own test projects, and projects
    /// that commit their data folder by mistake, can carry tracked files there, and a tracked file
    /// means the folder is somebody's work rather than the editor's.</para>
    /// </summary>
    public static BuildDirectoryKind Recognition(GodotProjectSurvey survey) => new()
    {
        DirectoryNames = [".godot", "godot"],
        RequiredSiblings = [GodotProjectSettings.ProjectFile],
        ConfirmsContents = (directory, _) => survey.IsDataFolder(directory),
        DeclinesTrackedDirectories = true,
    };

    private const string OtherReason =
        "Part of the project's data that Godot does not rebuild on its own, such as the editor's state "
        + "or the export credentials, so it is left alone.";

    private const string LinkReason = "A link rather than a folder, so what it points at was never examined.";

    private const string UnrecognisedImportReason =
        "Holds something Godot's importer does not write, so it is left alone.";

    private const string UnrecognisedShaderReason =
        "Holds something Godot's renderer does not write, so it is left alone.";

    private readonly string _folder;
    private readonly IReadOnlyList<(string Path, string Reason)> _others;
    private readonly bool _unreadable;

    // Read only when a row asks, so a pass with one row ticked does not walk the other's cache, and
    // kept once read, so the two rows share one reading.
    private CacheChild? _importCache;
    private CacheChild? _shaderCache;

    private GodotDataFolder(string folder, IReadOnlyList<(string Path, string Reason)> others, bool unreadable)
    {
        _folder = folder;
        _others = others;
        _unreadable = unreadable;
    }

    /// <summary>What one cache child turned out to be, when the data folder has one.</summary>
    private sealed record CacheChild(string Path, GodotCacheVerdict Verdict, string? Reason);

    /// <summary>Reads the entries of <paramref name="folder"/>, a recognised Godot 4 data folder.</summary>
    public static GodotDataFolder Of(string folder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var others = new List<(string Path, string Reason)>();

        using var entries = new DirectoryListing<(string Name, bool IsDirectory, bool IsLink)>(
            folder,
            static (ref System.IO.Enumeration.FileSystemEntry entry) =>
                (entry.FileName.ToString(), entry.IsDirectory, (entry.Attributes & FileAttributes.ReparsePoint) != 0));

        while (entries.MoveNext())
        {
            ct.ThrowIfCancellationRequested();

            var (name, isDirectory, isLink) = entries.Current;

            // A cache is named by the row that offers or declines it. A file or a link of a cache's
            // name is not one, and is named here.
            if (IsCache(name) && isDirectory && !isLink)
            {
                continue;
            }

            others.Add((Path.Combine(folder, name), isLink ? LinkReason : OtherReason));
        }

        return new GodotDataFolder(folder, others, entries.Outcome is not PathPresence.Present);
    }

    /// <summary>The import cache row's parts: <c>imported</c>, where every file in it is the importer's.</summary>
    public BuildDirectoryParts ImportCache(CancellationToken ct = default) => Parts(
        () => Read(ref _importCache, () => Child(ImportCacheName, path => GodotCacheContents.ImportCache(path, allowIgnoreFile: false, ct), UnrecognisedImportReason)),
        "Godot imported assets");

    /// <summary>The shader cache row's parts: <c>shader_cache</c>, where every file in it is the renderer's.</summary>
    public BuildDirectoryParts ShaderCache(CancellationToken ct = default) => Parts(
        () => Read(ref _shaderCache, () => Child(ShaderCacheName, path => GodotCacheContents.ShaderCache(path, ct), UnrecognisedShaderReason)),
        "Godot shader cache");

    /// <summary>
    /// The reading in <paramref name="slot"/>, or a new one kept there. A reading that raced another is
    /// discarded, so every caller gets the one kept, and a read that was cancelled keeps nothing.
    /// </summary>
    private static CacheChild Read(ref CacheChild? slot, Func<CacheChild> read)
    {
        if (Volatile.Read(ref slot) is { } kept)
        {
            return kept;
        }

        var reading = read();
        return Interlocked.CompareExchange(ref slot, reading, null) ?? reading;
    }

    private BuildDirectoryParts Parts(Func<CacheChild> child, string what)
    {
        // A data folder Windows would not list whole offers nothing: a cache it did not see cannot be
        // told apart from one it did not reach.
        if (_unreadable)
        {
            return new BuildDirectoryParts([], [], [_folder]);
        }

        return child() switch
        {
            { Verdict: GodotCacheVerdict.Recognised } cache =>
                new BuildDirectoryParts([new BuildDirectoryPart(cache.Path, what, [])], _others, []),
            { Verdict: GodotCacheVerdict.Unreadable } cache =>
                new BuildDirectoryParts([], _others, [cache.Path]),
            { Reason: { } reason } cache =>
                new BuildDirectoryParts([], [.. _others, (cache.Path, reason)], []),
            _ => new BuildDirectoryParts([], _others, []),
        };
    }

    /// <summary>
    /// The cache child of <paramref name="name"/>, or one with no reason where the data folder has
    /// none. Probed rather than taken from the listing, because the listing kept no case of the name.
    /// </summary>
    private CacheChild Child(string name, Func<string, GodotCacheVerdict> check, string unrecognisedReason)
    {
        var path = Path.Combine(_folder, name);

        return LongPath.ProbeDirectory(path, out var isLink) switch
        {
            // A link of this name was named as left alone by the listing, and so was a file.
            PathPresence.Present when isLink is false => check(path) switch
            {
                GodotCacheVerdict.Recognised => new CacheChild(path, GodotCacheVerdict.Recognised, null),
                GodotCacheVerdict.Unreadable => new CacheChild(path, GodotCacheVerdict.Unreadable, null),
                _ => new CacheChild(path, GodotCacheVerdict.Unrecognised, unrecognisedReason),
            },
            PathPresence.Refused => new CacheChild(path, GodotCacheVerdict.Unreadable, null),
            _ => new CacheChild(path, GodotCacheVerdict.Unrecognised, Reason: null),
        };
    }

    private static bool IsCache(string name) =>
        name.Equals(ImportCacheName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(ShaderCacheName, StringComparison.OrdinalIgnoreCase);
}
