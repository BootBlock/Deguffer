using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What a folder Godot's caches are kept in turned out to hold.</summary>
public enum GodotCacheVerdict
{
    /// <summary>Every entry is one the engine writes there, in the shape it writes it.</summary>
    Recognised,

    /// <summary>Something in it is not what the engine writes, so the folder is not offered (§5.2).</summary>
    Unrecognised,

    /// <summary>Windows would not list all of it, so what it holds was never seen.</summary>
    Unreadable,
}

/// <summary>
/// Whether a Godot import cache or shader cache holds only what the engine writes into it, read entry
/// by entry. A name and a <c>project.godot</c> beside the data folder say a Godot project is here, and
/// this says that the folder about to be removed is the engine's output and nothing else.
///
/// <para><b>The import cache.</b> The importer saves every product of one source file under
/// <c>&lt;file&gt;-&lt;md5 of its resource path&gt;</c>, adding the product's extension, and writes a
/// <c>.md5</c> of the same base beside them once the import finishes
/// (<c>get_import_base_path</c> in <see href="https://github.com/godotengine/godot/blob/master/core/io/resource_importer.cpp">resource_importer.cpp</see>,
/// <c>_reimport_file</c> in <see href="https://github.com/godotengine/godot/blob/master/editor/file_system/editor_file_system.cpp">editor_file_system.cpp</see>).
/// The hash is lower-case hexadecimal. So every file must be <c>&lt;base&gt;-&lt;32 hex&gt;.&lt;rest&gt;</c>,
/// and every file but a <c>.md5</c> must have the <c>.md5</c> of its base beside it. The engine writes
/// through a temporary file named after the file it replaces, which the same rule accepts, and
/// creates no folders here, so a folder inside declines the cache. Godot 3 kept the same cache, with
/// the same names, as the whole data folder, and its editor wrote a <c>.gdignore</c> into it.</para>
///
/// <para><b>The shader cache.</b> The renderer keeps one file per shader variant at
/// <c>&lt;shader&gt;\&lt;sha256 of its group&gt;\&lt;sha1 of its version&gt;.&lt;api&gt;.cache</c>,
/// with the API name lower-cased. Earlier Godot 4 releases left the API name out, and the OpenGL
/// renderer still does (<see href="https://github.com/godotengine/godot/blob/master/servers/rendering/renderer_rd/shader_rd.cpp">shader_rd.cpp</see>,
/// <see href="https://github.com/godotengine/godot/blob/master/drivers/gles3/shader_gles3.cpp">shader_gles3.cpp</see>).
/// The shader's folder is named after its class, which no rule here predicts, so only the two levels
/// below it are read by shape.</para>
///
/// <para>A link anywhere declines the folder: removing it would follow nothing, but what it points at
/// was never examined, and a cache the engine wrote holds none.</para>
/// </summary>
public static partial class GodotCacheContents
{
    /// <summary>The file Godot 3's editor wrote into its data folder to keep its file system scan out.</summary>
    public const string IgnoreFile = ".gdignore";

    /// <summary>Whether the import cache at <paramref name="folder"/> holds only what the importer writes.</summary>
    /// <param name="allowIgnoreFile">
    /// Whether a <see cref="IgnoreFile"/> is accepted, which it is in Godot 3's data folder and nowhere
    /// in Godot 4's <c>imported</c>.
    /// </param>
    public static GodotCacheVerdict ImportCache(string folder, bool allowIgnoreFile, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var bases = new HashSet<string>(StringComparer.Ordinal);
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var recognised = true;

        using var entries = new DirectoryListing<(string Name, bool IsDirectory, bool IsLink)>(
            folder,
            static (ref System.IO.Enumeration.FileSystemEntry entry) =>
                (entry.FileName.ToString(), entry.IsDirectory, (entry.Attributes & FileAttributes.ReparsePoint) != 0));

        while (recognised && entries.MoveNext())
        {
            ct.ThrowIfCancellationRequested();

            var (name, isDirectory, isLink) = entries.Current;

            if (isDirectory || isLink)
            {
                recognised = false;
            }
            else if (allowIgnoreFile && name.Equals(IgnoreFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            else if (ImportedFile().Match(name) is { Success: true } match)
            {
                var stem = match.Groups["base"].Value;

                if (match.Groups["rest"].Value == "md5")
                {
                    bases.Add(stem);
                }
                else
                {
                    needed.Add(stem);
                }
            }
            else
            {
                recognised = false;
            }
        }

        if (!recognised)
        {
            return GodotCacheVerdict.Unrecognised;
        }

        if (entries.Outcome is not PathPresence.Present)
        {
            return GodotCacheVerdict.Unreadable;
        }

        return needed.IsSubsetOf(bases) ? GodotCacheVerdict.Recognised : GodotCacheVerdict.Unrecognised;
    }

    /// <summary>Whether the shader cache at <paramref name="folder"/> holds only what the renderer writes.</summary>
    public static GodotCacheVerdict ShaderCache(string folder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var shaders = Folders(folder);

        if (shaders.Verdict is not GodotCacheVerdict.Recognised)
        {
            return shaders.Verdict;
        }

        foreach (var shader in shaders.Names)
        {
            var groups = Folders(Path.Combine(folder, shader));

            if (groups.Verdict is not GodotCacheVerdict.Recognised)
            {
                return groups.Verdict;
            }

            foreach (var group in groups.Names)
            {
                ct.ThrowIfCancellationRequested();

                if (!ShaderGroup().IsMatch(group))
                {
                    return GodotCacheVerdict.Unrecognised;
                }

                var variants = Files(Path.Combine(folder, shader, group));

                if (variants is not GodotCacheVerdict.Recognised)
                {
                    return variants;
                }
            }
        }

        return GodotCacheVerdict.Recognised;
    }

    /// <summary>The folders directly inside <paramref name="folder"/>, which must hold nothing else.</summary>
    private static (GodotCacheVerdict Verdict, IReadOnlyList<string> Names) Folders(string folder)
    {
        var scan = ChildDirectories.Under(folder);

        if (scan.Unreadable)
        {
            return (GodotCacheVerdict.Unreadable, []);
        }

        if (scan.Links.Count > 0 || !HoldsOnlyFolders(folder))
        {
            return (GodotCacheVerdict.Unrecognised, []);
        }

        return (GodotCacheVerdict.Recognised, [.. scan.Directories.Select(directory => directory.Name)]);
    }

    /// <summary>
    /// Whether <paramref name="folder"/> holds no file. A failed listing reads as false: the folder is
    /// then declined rather than reported unreadable, which costs disk space and never data.
    /// </summary>
    private static bool HoldsOnlyFolders(string folder)
    {
        using var files = new DirectoryListing<bool>(
            folder,
            static (ref System.IO.Enumeration.FileSystemEntry entry) => true,
            static (ref System.IO.Enumeration.FileSystemEntry entry) => !entry.IsDirectory);

        return !files.MoveNext() && files.Outcome is PathPresence.Present;
    }

    /// <summary>Whether every entry in one shader group's folder is a variant file the renderer writes.</summary>
    private static GodotCacheVerdict Files(string folder)
    {
        using var entries = new DirectoryListing<(string Name, bool IsDirectory, bool IsLink)>(
            folder,
            static (ref System.IO.Enumeration.FileSystemEntry entry) =>
                (entry.FileName.ToString(), entry.IsDirectory, (entry.Attributes & FileAttributes.ReparsePoint) != 0));

        while (entries.MoveNext())
        {
            var (name, isDirectory, isLink) = entries.Current;

            if (isDirectory || isLink || !ShaderVariant().IsMatch(name))
            {
                return GodotCacheVerdict.Unrecognised;
            }
        }

        return entries.Outcome is PathPresence.Present ? GodotCacheVerdict.Recognised : GodotCacheVerdict.Unreadable;
    }

    /// <summary>
    /// One file the importer wrote: its base, ending in the hash, and the rest of its name. The base is
    /// greedy, so it ends at the last hash in the name, which is the one the importer appended.
    /// </summary>
    [GeneratedRegex(@"^(?<base>.+-[0-9a-f]{32})\.(?<rest>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ImportedFile();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShaderGroup();

    /// <summary>A variant, with or without the API name, or the temporary file the engine writes it through.</summary>
    [GeneratedRegex(@"^[0-9a-f]{40}(\.[a-z0-9_]+)?\.cache([0-9]+\.tmp)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ShaderVariant();
}
