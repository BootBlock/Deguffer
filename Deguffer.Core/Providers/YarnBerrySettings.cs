using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>What Berry's settings say about its global folder and its cache clean.</summary>
/// <param name="Answered">Whether Berry answered the global folder question with a string.</param>
/// <param name="GlobalFolder">
/// The global folder Berry reports, as a full path, or null where it said nothing or named something
/// that is not a full path.
/// </param>
/// <param name="CleanDisabled">
/// Whether Berry's settings turn its cache clean off (<c>enableCacheClean: false</c>, from Yarn 4.6),
/// which says the cache is not to be cleared.
/// </param>
internal sealed record YarnBerrySettings(bool Answered, string? GlobalFolder, bool CleanDisabled)
{
    /// <summary>
    /// Ask <paramref name="yarn"/>, a Berry, both questions at once. The script its <c>yarnPath</c>
    /// names was asked with its version (<see cref="YarnVersionAnswer"/>).
    ///
    /// <para><c>config get</c> works outside a project in every Berry release and prints no banner.
    /// With <c>--json</c> it prints one JSON value, a path in Windows form resolved against the rc file
    /// that set it, and no colour
    /// (<a href="https://github.com/yarnpkg/berry/blob/75ebe063092f86e47e4afeaf1ae4bdf24402874b/packages/plugin-essentials/sources/commands/config/get.ts#L52-L98">get.ts</a>).
    /// It reads <c>YARN_GLOBAL_FOLDER</c> and the <c>.yarnrc.yml</c> in the profile, which a project's
    /// own settings can override for that project alone. A Yarn before 4.6 does not know
    /// <c>enableCacheClean</c>, fails the question, and has no such setting to honour, so that one
    /// decides nothing about whether Berry answered.</para>
    /// </summary>
    public static async Task<YarnBerrySettings> AskAsync(IProcessRunner runner, string yarn, CancellationToken ct)
    {
        var global = runner.RunAsync(yarn, "config get globalFolder --json", ToolRun.Query, ct);
        var clean = runner.RunAsync(yarn, "config get enableCacheClean --json", ToolRun.Query, ct);

        // Both are awaited together, so a fault in one never leaves the other unobserved.
        await Task.WhenAll(global, clean).ConfigureAwait(false);

        var folder = Value(await global.ConfigureAwait(false));

        return new YarnBerrySettings(
            folder is { ValueKind: JsonValueKind.String },
            PathOf(folder),
            CleanDisabled: Value(await clean.ConfigureAwait(false)) is { ValueKind: JsonValueKind.False });
    }

    /// <summary>The full path a <c>config get &lt;setting&gt; --json</c> printed, or null where it printed none.</summary>
    public static string? PathIn(CommandOutcome outcome) => PathOf(Value(outcome));

    private static string? PathOf(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } path ? LongPath.Configured(path.GetString()) : null;

    private static JsonElement? Value(CommandOutcome outcome)
    {
        if (outcome.LastLine is not { } line)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not an answer: --json prints one JSON value, and anything else is not it.
            return null;
        }
    }
}
