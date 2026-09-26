namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// Why a selection will not be removed, stated as soon as something is selected rather than after
/// the user tries.
///
/// <para>§7.1 asks for refusals to be "stated with their reason rather than by greying something
/// out", and a menu item that does nothing teaches nothing — least of all somebody reading a size
/// picture, who has no way to guess which of several rules applies.</para>
/// </summary>
public static class ExploreRefusalNote
{
    /// <summary>
    /// The sentence for <paramref name="items"/>, or null where nothing stands in the way.
    ///
    /// <para>One refusal is quoted whole, whether it is the only item or one of several, because the
    /// reason is the part a reader acts on. Several are counted instead: quoting each would bury the
    /// selection under paragraphs, and picking one out is how each reason is read.</para>
    /// </summary>
    /// <param name="verdict">
    /// What the policy says about a path, which is <see cref="ExploreActions.Verdict"/> on the page:
    /// a refusal while the policy is still being built as well as once it has been.
    /// </param>
    public static string? For(IReadOnlyList<ExploreItem> items, Func<string, ExploreVerdict> verdict)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(verdict);

        var reasons = items
            .Select(item => verdict(item.Path))
            .Where(answer => !answer.IsAllowed)
            .Select(answer => answer.Reason)
            .ToList();

        return (items.Count, reasons) switch
        {
            (_, []) => null,
            (1, [var only]) => only,
            (var count, [var only]) => $"One of these {count} items will not be removed: {only}",
            (var count, _) => $"{reasons.Count} of these {count} items will not be removed. Select them "
                              + "one at a time to see why.",
        };
    }
}
