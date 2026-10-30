using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One entry in a list Scoop takes the first set value of, such as <c>$env:SCOOP_CACHE,
/// (get_config CACHE_PATH), "$scoopdir\cache"</c>. Unset where Scoop's <c>Where-Object { $_ }</c>
/// would pass over it (absent, null or empty), and <see cref="WhyUnfollowed"/> where it is set to
/// something Deguffer cannot read as Scoop does.
/// </summary>
internal readonly record struct ScoopValue(string? Text, string? WhyUnfollowed)
{
    public bool IsSet => Text is not null || WhyUnfollowed is not null;

    public static ScoopValue From(string? text) => new(string.IsNullOrEmpty(text) ? null : text, null);
}

/// <summary>
/// The two settings in Scoop's <c>config.json</c> that move its folders, read as Scoop's
/// <c>load_cfg</c> and <c>get_config</c> read them (<c>lib/core.ps1</c> at
/// <c>e6aa3b366bdee8ed138c1e0f7b85192ebdd35d0f</c>): the file through <c>ConvertFrom-Json</c>, and
/// each setting by its lower-cased name through PowerShell's member access, which ignores case.
///
/// <para><b>Nothing else in the file is read.</b> It can hold a GitHub token and a proxy password
/// beside these two.</para>
/// </summary>
internal sealed record ScoopSettings(ScoopValue RootPath, ScoopValue CachePath)
{
    /// <summary>Far beyond any settings file Scoop writes, which holds a few dozen short values.</summary>
    private const int MaximumBytes = 256 * 1024;

    /// <summary>The settings of a machine with no settings file, where Scoop reads every value as unset.</summary>
    public static ScoopSettings None { get; } = new(default, default);

    /// <summary>
    /// The settings in <paramref name="file"/>, <see cref="None"/> where there is no such file, or
    /// null with the sentence saying why it could not be read as Scoop reads it.
    ///
    /// <para><b>A file Scoop fails to read is declined, not defaulted.</b> Scoop reports the error
    /// and goes on with no settings, so it is tempting to do the same. But the two parsers do not fail
    /// on the same files: Windows PowerShell rejects comments that PowerShell 7 accepts, and the shim
    /// runs whichever of the two is installed. So a file Deguffer cannot parse may be one Scoop reads,
    /// and the cache it names would be a folder Deguffer never examined.</para>
    /// </summary>
    public static ScoopSettings? Read(string file, out string why)
    {
        why = string.Empty;

        switch (LongPath.ProbeEntry(file, out var isDirectory))
        {
            case PathPresence.Absent:
                return None;

            case PathPresence.Refused:
                why = Declined(new UnreadFile(file, Unreached: true).Opening("Scoop's settings file"));
                return null;
        }

        using var document = isDirectory is true ? null : BoundedJsonFile.Read(file, MaximumBytes);

        if (document is null)
        {
            why = Declined(new UnreadFile(file, Unreached: false).Opening("Scoop's settings file"));
            return null;
        }

        // ConvertFrom-Json refuses a document holding two keys that differ only in case, at any depth,
        // and Scoop then reads no setting at all. System.Text.Json accepts it, so this is checked here.
        if (HasRepeatedKey(document.RootElement))
        {
            why = Declined($"Scoop's settings file at '{file}' names the same setting twice, which PowerShell "
                + "refuses to read");
            return null;
        }

        return new ScoopSettings(Setting(document.RootElement, "root_path"), Setting(document.RootElement, "cache_path"));
    }

    private static string Declined(string opening) =>
        $"{opening}, so Deguffer cannot tell where Scoop keeps its cache, and leaves it alone.";

    /// <summary>
    /// One setting. A string is the value, as is a missing or null one by being unset. Anything else
    /// becomes text on its way into <c>Get-AbsolutePath</c>, or is passed over where PowerShell reads
    /// it as false, and neither is a folder anybody set, so it is declined.
    /// </summary>
    private static ScoopValue Setting(JsonElement settings, string name)
    {
        foreach (var property in settings.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => ScoopValue.From(property.Value.GetString()),
                JsonValueKind.Null => default,
                _ => new ScoopValue(null, $"Scoop's {name} setting is not a folder, so Deguffer cannot tell "
                    + "where Scoop keeps its cache, and leaves it alone."),
            };
        }

        return default;
    }

    private static bool HasRepeatedKey(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object =>
            element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != element.EnumerateObject().Count()
            || element.EnumerateObject().Any(p => HasRepeatedKey(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasRepeatedKey),
        _ => false,
    };
}
