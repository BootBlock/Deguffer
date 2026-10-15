using Deguffer.Core.Providers;

namespace Deguffer.Core.Choosing;

/// <summary>One category's providers, as the list of what to scan draws them under its heading.</summary>
public sealed record ProviderGroup(ProviderCategory Category, IReadOnlyList<ICleanupProvider> Providers);

/// <summary>
/// Which providers the list of what to scan shows, and under which heading.
///
/// <para>Grouped by <see cref="ProviderCategory"/> in the category's own order, so the headings stand
/// still while the reader searches, and sorted by name inside each, because a reader looking for one
/// tool looks for it by name. A category with nothing to show is left out rather than drawn as an
/// empty heading.</para>
/// </summary>
public static class ProviderListing
{
    public static string Title(ProviderCategory category) => category switch
    {
        ProviderCategory.TemporaryFiles => "Temporary files",
        ProviderCategory.Windows => "Windows",
        ProviderCategory.InstallersAndUpdates => "Installers and updates",
        ProviderCategory.Browsers => "Browsers",
        ProviderCategory.Games => "Games",
        ProviderCategory.MediaAndCreative => "Media and creative apps",
        ProviderCategory.PackageCaches => "Package caches",
        ProviderCategory.ProjectBuildOutput => "Project build output",
        ProviderCategory.DeveloperTools => "Developer tools",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    /// <summary>
    /// <paramref name="providers"/> grouped, keeping only those <paramref name="search"/> matches.
    /// </summary>
    /// <param name="search">
    /// Words to look for, each anywhere in a provider's name, in the application its description
    /// names, or in its category's heading (<see cref="SearchWords"/>). Blank shows every provider.
    /// A heading matching shows every provider under it, so typing "games" brings up the whole group
    /// rather than the rows that happen to say "game".
    /// </param>
    public static IReadOnlyList<ProviderGroup> Group(IEnumerable<ICleanupProvider> providers, string? search)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var words = new SearchWords(search);

        return
        [
            .. providers
                .Where(provider => words.AllMentioned(word => Mentions(provider, word)))
                .GroupBy(provider => provider.Description.Category)
                .OrderBy(group => group.Key)
                .Select(group => new ProviderGroup(
                    group.Key,
                    [.. group.OrderBy(provider => provider.Name, StringComparer.CurrentCultureIgnoreCase)])),
        ];
    }

    private static bool Mentions(ICleanupProvider provider, string word) =>
        SearchWords.Contains(provider.Name, word)
        || SearchWords.Contains(provider.Description.Application, word)
        || SearchWords.Contains(Title(provider.Description.Category), word);
}
