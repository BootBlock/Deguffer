namespace Deguffer.Core.Choosing;

/// <summary>
/// What a search box asks of each thing it filters: every word typed has to appear somewhere in it,
/// in any order and regardless of case. One rule for every search box, so "steam cache" finds the
/// same things wherever it is typed.
///
/// <para>Where the words may appear is the caller's: each list searches what its reader can see. See
/// <see cref="ItemFilter"/> and <see cref="ProviderListing"/>.</para>
/// </summary>
public sealed class SearchWords
{
    private readonly string[] _words;

    public SearchWords(string? query) =>
        _words = query?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];

    /// <summary>Whether nothing was typed, so everything shows.</summary>
    public bool IsEmpty => _words.Length == 0;

    /// <summary>Whether every word is one that <paramref name="mentions"/> finds.</summary>
    public bool AllMentioned(Func<string, bool> mentions)
    {
        ArgumentNullException.ThrowIfNull(mentions);

        foreach (var word in _words)
        {
            if (!mentions(word))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="text"/> holds <paramref name="word"/>, regardless of case.</summary>
    public static bool Contains(string? text, string word) =>
        text is not null && text.Contains(word, StringComparison.OrdinalIgnoreCase);
}
