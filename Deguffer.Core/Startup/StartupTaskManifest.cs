using System.Xml;
using System.Xml.Linq;

namespace Deguffer.Core.Startup;

/// <summary>One startup task a package's manifest declares.</summary>
/// <param name="TaskId">The identifier Windows keeps the task's state under.</param>
/// <param name="DisplayName">
/// The name the manifest gives it, or null where it gives none or gives a resource reference, which
/// only the package's own resources can turn into words.
/// </param>
/// <param name="Executable">The program it starts, relative to the package's folder, or null where the manifest names none.</param>
public sealed record DeclaredStartupTask(string TaskId, string? DisplayName, string? Executable);

/// <summary>
/// Reads the startup tasks out of an <c>AppxManifest.xml</c> (§7.5). Windows documents the
/// <c>windows.startupTask</c> extension, in the <c>uap5</c> namespace for a packaged application and
/// the <c>desktop</c> namespace for a packaged desktop program, so the extension is found by its
/// category and its element by name, in whichever namespace the manifest put them.
/// </summary>
public static class StartupTaskManifest
{
    private const string Category = "windows.startupTask";

    /// <summary>
    /// Every startup task <paramref name="manifest"/> declares, in document order, or none where it is
    /// not a manifest that can be read.
    /// </summary>
    public static IReadOnlyList<DeclaredStartupTask> Read(string manifest)
    {
        XDocument document;

        try
        {
            // No DTD and no resolver: a manifest is data from a package, never a reason to fetch
            // anything.
            using var reader = XmlReader.Create(new StringReader(manifest), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });

            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return [];
        }

        var tasks = new List<DeclaredStartupTask>();

        foreach (var extension in document.Descendants().Where(IsStartupExtension))
        {
            var application = extension.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Application");
            var executable = Text(extension.Attribute("Executable")) ?? Text(application?.Attribute("Executable"));

            foreach (var task in extension.Elements().Where(e => e.Name.LocalName == "StartupTask"))
            {
                if (Text(task.Attribute("TaskId")) is { } id)
                {
                    var name = Text(task.Attribute("DisplayName"));

                    tasks.Add(new DeclaredStartupTask(id, IsResource(name) ? null : name, executable));
                }
            }
        }

        return tasks;
    }

    /// <summary>
    /// What Windows' <c>StartupTaskState</c> number means here. Documented: 0 is disabled, 1 disabled by
    /// the user, 2 enabled, 3 disabled by policy and 4 enabled by policy. Any other number, or none,
    /// says nothing (§7.5).
    /// </summary>
    public static StartupState StateOf(int? number) => number switch
    {
        0 or 1 or 3 => StartupState.Disabled,
        2 or 4 => StartupState.Starts,
        _ => StartupState.Unknown,
    };

    private static bool IsStartupExtension(XElement element) =>
        element.Name.LocalName == "Extension"
        && string.Equals(Text(element.Attribute("Category")), Category, StringComparison.Ordinal);

    private static bool IsResource(string? name) =>
        name is not null && name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase);

    private static string? Text(XAttribute? attribute) =>
        string.IsNullOrWhiteSpace(attribute?.Value) ? null : attribute.Value.Trim();
}
