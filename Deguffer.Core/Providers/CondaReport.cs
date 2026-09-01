using System.Text;
using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>Where conda keeps things, read from <c>conda info --json</c>.</summary>
/// <param name="RootPrefix">The installation itself, holding the base environment.</param>
/// <param name="PackageCacheDirs">The <c>pkgs</c> directories the clean command operates on.</param>
/// <param name="EnvironmentDirs">Where environments live — §5.6's survivors, never targets.</param>
internal sealed record CondaInstallation(
    string? RootPrefix,
    IReadOnlyList<string> PackageCacheDirs,
    IReadOnlyList<string> EnvironmentDirs);

/// <summary>
/// What conda's own dry run says its clean would free, in bytes. Only the tarball and package
/// categories carry sizes in the report; the index cache is listed by path alone, so the caller
/// measures that part itself.
/// </summary>
internal sealed record CondaCleanPreview(long TarballBytes, long PackageBytes);

/// <summary>
/// Reads conda's machine-readable output. Typed and tolerant in one place, because both answers
/// share the same hazard: a dry run ends by raising an exit exception whose handling has moved
/// between conda versions, so the JSON object may arrive with trailing text after it. The first
/// complete object is the report, whatever follows it.
/// </summary>
internal static class CondaReport
{
    /// <summary>
    /// The paths in the answer are configuration in §5.2's sense, so each goes through
    /// <see cref="LongPath.Configured"/> and an unusable one is dropped rather than kept as text.
    /// </summary>
    public static CondaInstallation? TryReadInstallation(string standardOutput)
    {
        using var document = TryParseFirstObject(standardOutput);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;

        return new CondaInstallation(
            root.TryGetProperty("root_prefix", out var prefix) && prefix.ValueKind == JsonValueKind.String
                ? LongPath.Configured(prefix.GetString())
                : null,
            ReadPaths(root, "pkgs_dirs"),
            ReadPaths(root, "envs_dirs"));
    }

    /// <summary>
    /// Null when the report cannot be read or does not claim success — and the caller then offers
    /// nothing, because the only other figure available is a naive measure of the package caches,
    /// which counts everything the environments still hard-link (§5.4).
    /// </summary>
    public static CondaCleanPreview? TryReadCleanPreview(string standardOutput)
    {
        using var document = TryParseFirstObject(standardOutput);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;

        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        return new CondaCleanPreview(
            ReadTotalSize(root, "tarballs"),
            ReadTotalSize(root, "packages"));
    }

    /// <summary>A category's <c>total_size</c>, or zero where conda reported none.</summary>
    private static long ReadTotalSize(JsonElement root, string category) =>
        root.TryGetProperty(category, out var element)
        && element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("total_size", out var size)
        && size.TryGetInt64(out var bytes)
            ? bytes
            : 0;

    private static IReadOnlyList<string> ReadPaths(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => LongPath.Configured(item.GetString()))
                .OfType<string>(),
        ];
    }

    /// <summary>
    /// The first complete JSON object in <paramref name="text"/>, or null when there is none.
    /// <see cref="JsonDocument.TryParseValue"/> reads exactly one value and never touches what
    /// trails it, which is the tolerance the dry run needs.
    /// </summary>
    private static JsonDocument? TryParseFirstObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text[start..]));

            return JsonDocument.TryParseValue(ref reader, out var document) ? document : null;
        }
        catch (JsonException)
        {
            // Output that opened a brace and never finished it — a crash mid-report. No report.
            return null;
        }
    }
}
