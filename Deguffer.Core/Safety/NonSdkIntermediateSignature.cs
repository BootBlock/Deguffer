using System.Buffers;
using System.Text;
using System.Xml;
using Deguffer.Core.Providers;

namespace Deguffer.Core.Safety;

/// <summary>
/// Recognises a directory as the intermediate output of an old-style (non-SDK) .NET project: a
/// <c>.csproj</c>, <c>.vbproj</c> or <c>.fsproj</c> in the MSBuild 2003 namespace, whose packages,
/// if any, come through <c>packages.config</c>.
///
/// <para><b>Why a second recogniser and not a relaxed first one.</b> Such a project never writes
/// <c>project.assets.json</c> or the generated NuGet imports, so
/// <see cref="DotNetIntermediateSignature"/>'s three sources do not exist for it. Its rule stays as
/// strict as it was, and this one rests on the evidence an old-style build does leave.</para>
///
/// <para><b>The evidence, all of which must agree.</b></para>
/// <list type="number">
/// <item>The <c>obj</c> root holds directories and nothing else. Of the old-style directories
/// surveyed, not one held a file at its root, so a file there is something this rule has not seen
/// MSBuild write.</item>
/// <item>Every directory below it is accounted for: a configuration directory
/// (<c>obj\Debug</c>), or a platform directory (<c>obj\x64</c>) holding only configuration
/// directories.</item>
/// <item>At least one configuration directory carries the build's own record of what it wrote,
/// <c>&lt;project&gt;.FileListAbsolute.txt</c>, and that record names a path inside that very
/// directory. Every one surveyed did.</item>
/// <item>A configuration directory with no such record holds only what a design-time build
/// writes, from a closed list. Visual Studio creates these for every configuration and platform it
/// has shown, whether or not anything was built there, and they were common among the
/// directories surveyed.</item>
/// <item>Every bookkeeping file names the same project, and that project file sits in the parent
/// as an old-style project.</item>
/// </list>
///
/// <para><b>Closing the copied-tree hole without a manifest.</b> A source tree copied for backup
/// keeps its file lists, and they name the original's paths. Requiring each list to name a path
/// inside the directory in hand turns that into the check
/// <see cref="DotNetIntermediateSignature"/> makes with the parent: the copy's lists point at the
/// original, never at the copy, so the copy is unrecognised. The paths are compared as text and
/// never resolved, so a list cannot vouch for anything but the directory it was found in. A
/// project moved or reached through another drive letter since its last build is unrecognised
/// too, which costs only the space.</para>
///
/// <para><b>What a file list names outside <c>obj</c> changes nothing.</b> It names the output
/// directory as well, and it is read here as evidence of identity only, never as a list of things
/// to remove. Only the <c>obj</c> directory is a target; <c>bin</c> stays protected and out of
/// scope.</para>
/// </summary>
public static class NonSdkIntermediateSignature
{
    /// <summary>The namespace every old-style project's root element is in.</summary>
    private const string LegacyNamespace = "http://schemas.microsoft.com/developer/msbuild/2003";

    private const string FileListSuffix = ".FileListAbsolute.txt";

    /// <summary>
    /// Bookkeeping files MSBuild names after the project file, as the suffix after its name. Each
    /// one found must name the same project, so a directory shared by two projects, or holding one
    /// project's records under another's, is unrecognised.
    ///
    /// <para>The reference caches have three spellings: current MSBuild writes
    /// <c>Example.vbproj.AssemblyReference.cache</c>, older ones
    /// <c>Example.vbprojAssemblyReference.cache</c> and
    /// <c>Example.vbprojResolveAssemblyReference.cache</c>. The first match wins, so each suffix comes
    /// before any shorter one that ends it.</para>
    /// </summary>
    private static readonly string[] BookkeepingSuffixes =
    [
        FileListSuffix,
        ".CoreCompileInputs.cache",
        ".AssemblyReference.cache",
        "ResolveAssemblyReference.cache",
        "AssemblyReference.cache",
    ];

    /// <summary>
    /// What a design-time build writes into a configuration directory, by exact name. Measured, not
    /// assumed: these, a project-named <c>AssemblyReference.cache</c> or
    /// <c>CoreCompileInputs.cache</c>, a <c>*.AssemblyAttributes.cs</c>, generated
    /// <c>TemporaryGeneratedFile_*.cs</c> stubs and an empty <c>TempPE</c> were the whole of every
    /// such directory surveyed.
    /// </summary>
    private static readonly HashSet<string> DesignTimeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "DesignTimeResolveAssemblyReferences.cache",
        "DesignTimeResolveAssemblyReferencesInput.cache",
        "build.force",
    };

    private const string TempPE = "TempPE";

    /// <summary>
    /// The largest project file read. Real ones run to tens of kilobytes; one past this is not a
    /// project file anybody maintains by hand.
    /// </summary>
    private const int MaximumProjectBytes = 1024 * 1024;

    /// <summary>
    /// The largest file list read. A line is one path, so this is tens of thousands of files built
    /// into one configuration, past anything a single project writes.
    /// </summary>
    private const int MaximumFileListBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Characters no path holds. A line carrying one is not a line MSBuild wrote, and some of them
    /// make path parsing throw rather than answer.
    /// </summary>
    private static readonly SearchValues<char> InvalidPathCharacters = SearchValues.Create(Path.GetInvalidPathChars());

    private static readonly XmlReaderSettings ProjectReading = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    /// <summary>
    /// The project <paramref name="objDirectory"/> is intermediate output for, or null if its
    /// identity cannot be established — in which case the caller must leave it alone.
    /// </summary>
    public static RecognisedProject? TryRecognise(string objDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objDirectory);

        var obj = objDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(obj);

        if (string.IsNullOrEmpty(parent) || LongPath.IsReparsePoint(obj) || LongPath.IsReparsePoint(parent))
        {
            // A junction here would let deletion escape the directory that was examined.
            return null;
        }

        if (List(obj) is not { Count: > 0 } top || top.Any(entry => entry is not DirectoryInfo))
        {
            return null;
        }

        var evidence = new Evidence();

        foreach (var directory in top)
        {
            ct.ThrowIfCancellationRequested();

            if (!AccountFor(Path.Combine(obj, directory.Name), platformAllowed: true, evidence))
            {
                return null;
            }
        }

        if (!evidence.Bookkept || evidence.Project is not { } projectFileName)
        {
            return null;
        }

        var projectFile = Path.Combine(parent, projectFileName);

        return IsLegacyProject(projectFile) ? new RecognisedProject(projectFile, projectFileName) : null;
    }

    /// <summary>
    /// Whether <paramref name="directory"/> is a configuration directory, or (where
    /// <paramref name="platformAllowed"/>) a platform directory holding only configuration
    /// directories. What it proves goes into <paramref name="evidence"/>.
    /// </summary>
    private static bool AccountFor(string directory, bool platformAllowed, Evidence evidence)
    {
        if (List(directory) is not { } entries)
        {
            return false;
        }

        if (entries.Any(entry => entry.Name.EndsWith(FileListSuffix, StringComparison.OrdinalIgnoreCase)))
        {
            return IsBookkept(directory, entries, evidence);
        }

        if (entries.All(entry => IsDesignTimeEntry(directory, entry, evidence)))
        {
            return true;
        }

        return platformAllowed
            && entries.All(entry => entry is DirectoryInfo)
            && entries.All(entry => AccountFor(Path.Combine(directory, entry.Name), platformAllowed: false, evidence));
    }

    /// <summary>
    /// A configuration directory a build ran in. Its contents beyond the bookkeeping are not
    /// inspected, as <see cref="DotNetIntermediateSignature"/> does not inspect an SDK-style
    /// <c>obj</c>'s: the build's own record of writing here is the evidence.
    /// </summary>
    private static bool IsBookkept(string directory, IReadOnlyList<FileSystemInfo> entries, Evidence evidence)
    {
        // Every file list is named after its project, so once all the bookkeeping agrees there can be
        // only one.
        if (entries.FirstOrDefault(entry => entry is FileInfo
                && entry.Name.EndsWith(FileListSuffix, StringComparison.OrdinalIgnoreCase)) is not { } fileList
            || !entries.Where(entry => entry is FileInfo).All(entry => AgreesOnProject(entry.Name, evidence))
            || !NamesAPathInside(Path.Combine(directory, fileList.Name), directory))
        {
            return false;
        }

        evidence.Bookkept = true;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="entry"/> is one a design-time build writes. A project-named one must
    /// agree with every other on which project that is.
    /// </summary>
    private static bool IsDesignTimeEntry(string directory, FileSystemInfo entry, Evidence evidence)
    {
        if (entry is DirectoryInfo)
        {
            return entry.Name.Equals(TempPE, StringComparison.OrdinalIgnoreCase)
                && List(Path.Combine(directory, entry.Name)) is { Count: 0 };
        }

        var name = entry.Name;

        if (DesignTimeFiles.Contains(name)
            || name.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase)
            || IsGeneratedStub(name))
        {
            return true;
        }

        return ProjectNamedBy(name) is not null && AgreesOnProject(name, evidence);
    }

    /// <summary>
    /// <c>TemporaryGeneratedFile_&lt;guid&gt;.cs</c>, the empty stubs workflow and designer targets
    /// write. The GUID is parsed rather than matched loosely, so the name proves the generator.
    /// </summary>
    private static bool IsGeneratedStub(string name)
    {
        const string prefix = "TemporaryGeneratedFile_";
        const string suffix = ".cs";

        return name.Length > prefix.Length + suffix.Length
            && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length), out _);
    }

    /// <summary>
    /// False if <paramref name="fileName"/> is bookkeeping for a project other than the one already
    /// named. Anything else agrees, because it names no project.
    /// </summary>
    private static bool AgreesOnProject(string fileName, Evidence evidence)
    {
        if (ProjectNamedBy(fileName) is not { } project)
        {
            return true;
        }

        evidence.Project ??= project;

        return evidence.Project.Equals(project, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The project file a bookkeeping file is named after, or null if it is not bookkeeping or the
    /// name before the suffix is not a project file.
    /// </summary>
    private static string? ProjectNamedBy(string fileName)
    {
        foreach (var suffix in BookkeepingSuffixes)
        {
            if (fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var project = fileName[..^suffix.Length];

                return DotNetIntermediateSignature.ProjectExtensions.Contains(
                    Path.GetExtension(project), StringComparer.OrdinalIgnoreCase)
                    ? project
                    : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the file list at <paramref name="fileList"/> is a list of absolute paths, at least one
    /// of them inside <paramref name="directory"/>. Compared as text, never resolved.
    /// </summary>
    private static bool NamesAPathInside(string fileList, string directory)
    {
        if (BoundedFile.Read(fileList, MaximumFileListBytes) is not { } content)
        {
            return false;
        }

        var inside = false;

        foreach (var line in Encoding.UTF8.GetString(content.Span).Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.AsSpan().ContainsAny(InvalidPathCharacters) || !Path.IsPathFullyQualified(line))
            {
                // Not the file MSBuild writes, whose every line is an absolute path.
                return false;
            }

            inside |= LongPath.Contains(LongPath.Display(directory), LongPath.Display(line));
        }

        return inside;
    }

    /// <summary>Whether <paramref name="projectFile"/> is an old-style project, read from its root element.</summary>
    private static bool IsLegacyProject(string projectFile)
    {
        if (BoundedFile.Read(projectFile, MaximumProjectBytes) is not { } content)
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, ProjectReading);

            // An SDK-style project may carry the 2003 namespace too, so the Sdk attribute is what
            // separates them. One with an Sdk import instead writes a restore manifest at the obj
            // root, which the caller has already refused.
            return reader.MoveToContent() == XmlNodeType.Element
                && reader.LocalName == "Project"
                && reader.NamespaceURI == LegacyNamespace
                && reader.GetAttribute("Sdk") is null;
        }
        catch (XmlException)
        {
            // Not XML, or a document type declaration, which is refused rather than processed: the
            // project could not be confirmed, which is Tier 4.
            return false;
        }
    }

    /// <summary>
    /// Every entry of <paramref name="directory"/>, or null if it could not be listed to the end or
    /// holds a link. A link is a tree nobody examined, and a partial listing describes a directory
    /// nobody fully read.
    /// </summary>
    private static List<FileSystemInfo>? List(string directory)
    {
        var entries = new List<FileSystemInfo>();

        using var listing = DirectoryListing.Of(directory);

        while (listing.MoveNext())
        {
            if (listing.Current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            entries.Add(listing.Current);
        }

        return listing.Outcome == PathPresence.Present ? entries : null;
    }

    /// <summary>What the directories examined so far have established, shared across the walk.</summary>
    private sealed class Evidence
    {
        /// <summary>The project every bookkeeping file so far has named, once one has.</summary>
        public string? Project { get; set; }

        /// <summary>Whether a configuration directory carried a file list naming itself.</summary>
        public bool Bookkept { get; set; }
    }
}
