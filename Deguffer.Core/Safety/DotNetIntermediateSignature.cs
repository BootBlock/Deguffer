using System.Text.Json;

namespace Deguffer.Core.Safety;

/// <summary>The project a recognised <c>obj</c> directory was generated for.</summary>
/// <param name="ProjectFilePath">The project file itself, which must survive (§5.6).</param>
/// <param name="ProjectName">Its filename, for describing the step to the user.</param>
public sealed record RecognisedProject(string ProjectFilePath, string ProjectName);

/// <summary>
/// Recognises a directory as SDK-style .NET intermediate output by asking <em>which project
/// generated this, and does that project agree</em> — never by its name.
///
/// A name check is not enough, and that is measured rather than assumed: of 580 directories named
/// <c>obj</c> across one source root, 238 were not SDK-style intermediate output at all, and one
/// held third-party Wavefront models and materials that no build can regenerate. §5.2's dangerous
/// direction is treating an unknown thing as safe, so identity here is triangulated across three
/// independent sources that must all name the same project: the restore manifest, the generated
/// MSBuild imports, and a project file sitting beside the directory.
///
/// This is deliberately stricter than a correct <c>obj</c> needs. Every condition that fails means
/// Tier 4 and untouched, because the cost of being wrong is asymmetric — a missed directory costs
/// disk space, a wrong one costs data.
///
/// A separate type rather than a <see cref="ContentSignature"/>: that primitive asks whether every
/// entry matches a pattern and requires the directory to hold no subdirectories at all, which an
/// <c>obj</c> never satisfies because it always holds <c>Debug</c> or <c>Release</c>. Relaxing it
/// to fit would weaken the totality that <c>vscode-cpptools</c> depends on, and it still could not
/// express this rule, which is about name correspondence between files and a manifest.
/// </summary>
public static class DotNetIntermediateSignature
{
    /// <summary>
    /// Project file extensions whose <c>obj</c> this recognises. Deliberately not <c>.proj</c> or
    /// <c>.msbuildproj</c>: they exist in the wild but are rare enough that they were never
    /// observed being verified end-to-end here, and an unrecognised extension costs only the space.
    /// </summary>
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    /// <summary>
    /// Schema versions of <c>project.assets.json</c> whose <c>project.restore.projectPath</c> was
    /// confirmed to carry the meaning relied on below.
    ///
    /// Both are current: across 342 manifests surveyed, 323 were version 3 and 19 version 4, the
    /// latter written by newer SDKs. Accepting only one would silently stop recognising directories
    /// as toolchains update — a false negative rather than a dangerous one, but a growing one.
    /// A version outside this set is unrecognised, because a schema not read here is a schema whose
    /// fields have not been checked to mean what this rule assumes.
    /// </summary>
    private static readonly int[] KnownSchemaVersions = [3, 4];

    /// <summary>
    /// A ceiling on the manifest read into memory. Real ones run to about a megabyte; anything
    /// vastly larger is not the file this rule was written against, and refusing it is the safe
    /// direction as well as the cheap one.
    /// </summary>
    private const long MaximumManifestBytes = 64L * 1024 * 1024;

    /// <summary>
    /// The project <paramref name="objDirectory"/> is intermediate output for, or null if its
    /// identity cannot be established — in which case the caller must leave it alone.
    /// </summary>
    /// <remarks>
    /// Only the conventional layout is recognised: <c>&lt;project&gt;/obj/</c>, where the project
    /// file sits in the parent. .NET 8's <c>UseArtifactsOutput</c> relocates intermediates to a
    /// central <c>artifacts/obj/&lt;Project&gt;/</c> instead, and those stay Tier 4 until they have
    /// tests of their own — the manifest's own <c>projectPath</c> is the evidence that survives
    /// relocation, so adding them later is a second recogniser rather than a change to this one.
    ///
    /// Requiring the project file in the parent, rather than merely somewhere on disk, is what
    /// closes a hole found while surveying real directories: a source tree copied for backup keeps
    /// a manifest naming the <em>original</em> project, which still exists. Checking only that the
    /// named path exists would let a manifest vouch for a directory it has nothing to do with, so
    /// the check is against the directory actually in hand.
    /// </remarks>
    public static RecognisedProject? TryRecognise(string objDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objDirectory);

        var parent = Path.GetDirectoryName(objDirectory.TrimEnd(Path.DirectorySeparatorChar));

        if (string.IsNullOrEmpty(parent) || IsReparsePoint(objDirectory) || IsReparsePoint(parent))
        {
            // A junction here would let deletion escape the directory that was examined.
            return null;
        }

        ct.ThrowIfCancellationRequested();

        if (ReadProjectFileName(Path.Combine(objDirectory, "project.assets.json")) is not { } projectFileName)
        {
            return null;
        }

        var projectFile = Path.Combine(parent, projectFileName);

        // All three sources have to name the same project. The generated imports are matched on the
        // exact stem rather than on *.nuget.g.props: a directory holding imports for some other
        // project is evidence of a copied manifest, not of this project's output.
        if (!LongPath.FileExists(projectFile)
            || !LongPath.FileExists(Path.Combine(objDirectory, projectFileName + ".nuget.g.props"))
            || !LongPath.FileExists(Path.Combine(objDirectory, projectFileName + ".nuget.g.targets")))
        {
            return null;
        }

        return new RecognisedProject(projectFile, projectFileName);
    }

    /// <summary>
    /// The project filename the restore manifest names, or null if the manifest is missing,
    /// unreadable, of an unrecognised schema, or names something that is not a project file.
    ///
    /// Only the filename is taken. The manifest records an absolute path, and resolving that path
    /// would reintroduce the copied-tree hole the caller's parent check exists to close.
    /// </summary>
    private static string? ReadProjectFileName(string manifest)
    {
        try
        {
            var file = new FileInfo(LongPath.Extended(manifest));

            if (!file.Exists || file.Length > MaximumManifestBytes)
            {
                return null;
            }

            using var stream = file.OpenRead();
            using var document = JsonDocument.Parse(stream);

            if (TryGetProperty(document.RootElement, "version") is not { ValueKind: JsonValueKind.Number } version
                || !version.TryGetInt32(out var schema)
                || !KnownSchemaVersions.Contains(schema))
            {
                return null;
            }

            if (TryGetProperty(document.RootElement, "project") is not { } project
                || TryGetProperty(project, "restore") is not { } restore
                || TryGetProperty(restore, "projectPath") is not { ValueKind: JsonValueKind.String } path
                || path.GetString() is not { Length: > 0 } projectPath)
            {
                return null;
            }

            var fileName = Path.GetFileName(projectPath);

            return ProjectExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase)
                ? fileName
                : null;
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or JsonException
                                      or ArgumentException)
        {
            // Truncated, hand-edited, locked, or holding a path this platform cannot express.
            // Every one of them means the identity could not be established, which is Tier 4.
            return null;
        }
    }

    /// <summary>
    /// One property of a JSON object, or null if it is absent — or if the value being asked is not
    /// an object at all.
    ///
    /// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> is named as though it
    /// answers that question and does not: asked for a property of an array or a string it throws
    /// rather than returning false. A hand-edited manifest that is valid JSON of the wrong shape is
    /// exactly the input this rule has to survive, so the kind is checked rather than assumed.
    /// </summary>
    private static JsonElement? TryGetProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : null;

    private static bool IsReparsePoint(string directory)
    {
        try
        {
            var info = new DirectoryInfo(LongPath.Extended(directory));
            return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Cannot tell what it is, so cannot vouch for it.
            return true;
        }
    }
}
