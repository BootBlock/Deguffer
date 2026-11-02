using System.Text.Json;
using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>The symbol cache one Visual Studio instance's settings name.</summary>
/// <param name="SettingsFile">The settings file, for a note that names where the value came from.</param>
/// <param name="Folder">What the setting holds.</param>
public sealed record VisualStudioSymbolCacheSetting(string SettingsFile, string Folder);

/// <summary>Every Visual Studio instance's symbol cache setting this user's profile holds.</summary>
/// <param name="Settings">One for each instance whose settings file names a folder.</param>
/// <param name="Unread">Each settings file that is there and could not be read as JSON.</param>
/// <param name="WithoutSettingsFile">
/// Each instance folder with no settings file, as an older Visual Studio's is, whose setting Deguffer
/// cannot read.
/// </param>
public sealed record VisualStudioSymbolSettingsReading(
    IReadOnlyList<VisualStudioSymbolCacheSetting> Settings,
    IReadOnlyList<string> Unread,
    IReadOnlyList<string> WithoutSettingsFile);

/// <summary>
/// Reads "Cache symbols in this directory" from each Visual Studio instance's settings.
///
/// <para><b>Visual Studio 2026 keeps it in <c>settings.json</c></b> in the instance's folder,
/// <c>%LOCALAPPDATA%\Microsoft\VisualStudio\18.0_&lt;id&gt;</c>, as
/// <c>debugging.symbols.location.cachePath</c>. Observed, not documented: Microsoft documents only the
/// option and its automation property. The file opens with a comment, so it is read as JSON with
/// comments.</para>
///
/// <para><b>An older Visual Studio has no <c>settings.json</c></b>, and nothing documents where it keeps
/// the setting, so it is not read. Each such instance is reported, so the plan can say that a cache moved
/// in its options is not looked for.</para>
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
            return new VisualStudioSymbolSettingsReading([], [], []);
        }

        List<VisualStudioSymbolCacheSetting> settings = [];
        List<string> unread = [];
        List<string> withoutFile = [];

        foreach (var instance in ChildDirectories.Under(root).Directories.Where(folder => InstanceFolder().IsMatch(folder.Name)))
        {
            var folder = LongPath.Display(instance.FullName);
            var file = Path.Combine(folder, SettingsFileName);

            if (LongPath.ProbeEntry(file) is PathPresence.Absent)
            {
                withoutFile.Add(folder);
                continue;
            }

            using var document = BoundedJsonFile.Read(file, MaximumBytes, WithComments);

            if (document is null)
            {
                unread.Add(file);
                continue;
            }

            if (BoundedJsonFile.StringProperty(document.RootElement, CachePathKey) is { Length: > 0 } cache)
            {
                settings.Add(new VisualStudioSymbolCacheSetting(file, cache));
            }
        }

        return new VisualStudioSymbolSettingsReading(settings, unread, withoutFile);
    }
}
