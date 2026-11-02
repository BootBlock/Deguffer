using System.Text.Json;
using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>The symbol cache one Visual Studio instance's settings name.</summary>
/// <param name="SettingsFile">The settings file, for a note that names where the value came from.</param>
/// <param name="Folder">What the setting holds, or null where it names nothing.</param>
public sealed record VisualStudioSymbolCacheSetting(string SettingsFile, string? Folder);

/// <summary>Every Visual Studio instance's symbol cache setting this user's profile holds.</summary>
/// <param name="Settings">One for each instance whose settings file was read.</param>
/// <param name="Unread">Each settings file that is there and could not be read as JSON.</param>
public sealed record VisualStudioSymbolSettingsReading(
    IReadOnlyList<VisualStudioSymbolCacheSetting> Settings,
    IReadOnlyList<string> Unread);

/// <summary>
/// Reads "Cache symbols in this directory" from each Visual Studio instance's settings.
///
/// <para><b>Visual Studio 2026 keeps it in <c>settings.json</c></b> in the instance's folder,
/// <c>%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_&lt;id&gt;</c>, as
/// <c>debugging.symbols.location.cachePath</c>. Observed, not documented: Microsoft documents only the
/// option and its automation property. The file opens with a comment, so it is read as JSON with
/// comments.</para>
///
/// <para><b>Visual Studio 2022 keeps its settings in a private registry hive</b> that nothing documents,
/// so its setting is not read. Its default folder is the one Visual Studio 2026 uses, and the plan says
/// that a moved cache of an older Visual Studio is not looked for.</para>
/// </summary>
public static partial class VisualStudioSymbolSettings
{
    /// <summary>The setting, as Visual Studio 2026 names it in <c>settings.json</c>.</summary>
    public const string CachePathKey = "debugging.symbols.location.cachePath";

    public const string SettingsFileName = "settings.json";

    /// <summary>Far beyond any settings file observed, and small enough to read whole.</summary>
    private const int MaximumBytes = 4 * 1024 * 1024;

    private static readonly JsonDocumentOptions WithComments = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>An instance's folder: the major and minor version, and the instance's identifier.</summary>
    [GeneratedRegex(@"\A\d+\.\d+_[0-9A-Fa-f]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstanceFolder();

    /// <summary>The folder that holds one folder for each Visual Studio instance.</summary>
    public static string InstancesRoot(string localAppData) => Path.Combine(localAppData, "Microsoft", "VisualStudio");

    public static VisualStudioSymbolSettingsReading Read(string localAppData)
    {
        var root = InstancesRoot(localAppData);

        if (LongPath.ProbeDirectory(root) is not PathPresence.Present)
        {
            return new VisualStudioSymbolSettingsReading([], []);
        }

        List<VisualStudioSymbolCacheSetting> settings = [];
        List<string> unread = [];

        foreach (var instance in ChildDirectories.Under(root).Directories.Where(folder => InstanceFolder().IsMatch(folder.Name)))
        {
            var file = Path.Combine(LongPath.Display(instance.FullName), SettingsFileName);

            if (LongPath.ProbeEntry(file) is PathPresence.Absent)
            {
                continue;
            }

            using var document = BoundedJsonFile.Read(file, MaximumBytes, WithComments);

            if (document is null)
            {
                unread.Add(file);
                continue;
            }

            settings.Add(new VisualStudioSymbolCacheSetting(
                file,
                BoundedJsonFile.StringProperty(document.RootElement, CachePathKey) is { Length: > 0 } folder ? folder : null));
        }

        return new VisualStudioSymbolSettingsReading(settings, unread);
    }
}
