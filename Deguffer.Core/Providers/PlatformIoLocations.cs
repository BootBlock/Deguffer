using System.Text.Json;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where PlatformIO keeps things, as it reported them or as documented beneath it.
///
/// A type of its own rather than three fields on the provider, because reading a tool's own report
/// is a separate job from deciding what to offer out of it — the split
/// <see cref="CondaReport"/> already makes for the same reason.
/// </summary>
/// <param name="CoreDirectory">
/// The directory everything else sits in, and what §5.6's protected paths are built from. Getting
/// this wrong costs the negative assertion entirely: every path beneath a guessed core directory is
/// absent on a relocated install, and <see cref="Execution.ProtectedPath.ExistedBefore"/> then
/// records six checks as never present, which pass while establishing nothing.
/// </param>
/// <param name="CacheDirectory">The download cache, which is all <c>prune --cache</c> touches.</param>
/// <param name="PackagesDirectory">
/// The installed toolchains. Never a target: it is measured as the "before" figure for the package
/// prune's reclaim, and PlatformIO decides which packages inside it go.
/// </param>
internal sealed record PlatformIoLocations(
    string CoreDirectory,
    string CacheDirectory,
    string PackagesDirectory)
{
    /// <summary>
    /// The locations in <c>pio system info --json-output</c>, or the documented defaults beneath
    /// whatever it did not report.
    ///
    /// <para><c>--json-output</c> rather than scraping the human listing: the field names are part
    /// of a documented machine-readable contract, the alignment of the text table is not.</para>
    ///
    /// <para>Which fields come back varies by version — 6.1.19 reports <c>core_dir</c> and neither
    /// of the other two — so each answer falls back to the default beneath it rather than to
    /// nothing. Pass an empty string for <paramref name="json"/> where the command failed, which
    /// takes every default.</para>
    /// </summary>
    /// <param name="defaultCoreRoot">
    /// Where PlatformIO lives when it has not been asked, which is the caller's to know: it is built
    /// from the user's profile, and this type is given no environment to read one from.
    /// </param>
    public static PlatformIoLocations Read(string json, string defaultCoreRoot)
    {
        var reported = TryReadReport(json);
        var core = ReadPath(reported, "core_dir") ?? defaultCoreRoot;

        return new PlatformIoLocations(
            core,
            ReadPath(reported, "cache_dir") ?? Path.Combine(core, ".cache"),
            ReadPath(reported, "packages_dir") ?? Path.Combine(core, "packages"));
    }

    /// <summary>
    /// The report as a value that outlives the document holding it, which is what
    /// <see cref="JsonElement.Clone"/> is for. Three fields are read from it, and threading the
    /// document through three calls to keep it alive would put the disposal in the caller.
    /// </summary>
    private static JsonElement? TryReadReport(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            // An older PlatformIO that does not understand --json-output prints its usage text to
            // stdout and still exits zero, so malformed output here is an expected outcome rather
            // than a broken install. The documented locations are the honest fallback.
            return null;
        }
    }

    /// <summary>
    /// PlatformIO wraps each value in <c>{"value": …, "default": …}</c> in some versions and emits
    /// a bare string in others, so both shapes are read rather than assuming the current one.
    /// </summary>
    private static string? ReadPath(JsonElement? reported, string name)
    {
        if (reported is not { } root || !root.TryGetProperty(name, out var property))
        {
            return null;
        }

        var value = property.ValueKind == JsonValueKind.Object
            && property.TryGetProperty("value", out var wrapped)
                ? wrapped
                : property;

        return value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } path
            && Path.IsPathRooted(path)
                ? path
                : null;
    }
}
