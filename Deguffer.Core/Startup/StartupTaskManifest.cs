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

    private const string TaskElement = "StartupTask";

    /// <summary>No DTD and no resolver: a manifest is data from a package, never a reason to fetch anything.</summary>
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    /// <summary>
    /// Every startup task <paramref name="manifest"/> declares, in document order, or none where it is
    /// not a manifest that can be read.
    /// </summary>
    public static IReadOnlyList<DeclaredStartupTask> Read(string manifest)
    {
        // Nearly every package declares no task. XML allows no character reference in an element
        // name, so every manifest that declares one contains the element's local name literally,
        // whatever prefix it is written with. The category may be written with character
        // references, so it cannot serve as the filter.
        if (!manifest.Contains(TaskElement, StringComparison.Ordinal))
        {
            return [];
        }

        try
        {
            return ReadDeclared(manifest);
        }
        catch (XmlException)
        {
            return [];
        }
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

    /// <summary>
    /// Reads forward through the document and builds an element only for a startup extension. The
    /// whole document is still read to its end, so a manifest that is not well formed anywhere
    /// declares nothing, as it would if it were loaded whole.
    /// </summary>
    private static List<DeclaredStartupTask> ReadDeclared(string manifest)
    {
        var tasks = new List<DeclaredStartupTask>();

        // The executables of the Application elements the reader is inside, innermost on top, so an
        // extension that names no program takes its nearest application's.
        var applications = new Stack<(int Depth, string? Executable)>();

        using var reader = XmlReader.Create(new StringReader(manifest), Settings);

        reader.Read();

        while (!reader.EOF)
        {
            if (reader.NodeType is XmlNodeType.Element
                && reader.LocalName == "Extension"
                && string.Equals(Text(reader.GetAttribute("Category")), Category, StringComparison.Ordinal))
            {
                var executable = Text(reader.GetAttribute("Executable"))
                    ?? (applications.TryPeek(out var application) ? application.Executable : null);

                // Reading the element leaves the reader on the node after it.
                var extension = (XElement)XNode.ReadFrom(reader);

                AddTasks(extension, executable, tasks);
                continue;
            }

            if (reader.NodeType is XmlNodeType.Element && reader.LocalName == "Application" && !reader.IsEmptyElement)
            {
                applications.Push((reader.Depth, Text(reader.GetAttribute("Executable"))));
            }
            else if (reader.NodeType is XmlNodeType.EndElement
                && reader.LocalName == "Application"
                && applications.TryPeek(out var open)
                && open.Depth == reader.Depth)
            {
                applications.Pop();
            }

            reader.Read();
        }

        return tasks;
    }

    private static void AddTasks(XElement extension, string? executable, List<DeclaredStartupTask> tasks)
    {
        foreach (var task in extension.Elements().Where(e => e.Name.LocalName == TaskElement))
        {
            if (Text(task.Attribute("TaskId")?.Value) is { } id)
            {
                var name = Text(task.Attribute("DisplayName")?.Value);

                tasks.Add(new DeclaredStartupTask(id, IsResource(name) ? null : name, executable));
            }
        }
    }

    private static bool IsResource(string? name) =>
        name is not null && name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase);

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
