namespace Deguffer.Core.Text;

/// <summary>
/// Names in a sentence, in the form a reader expects rather than comma-separated throughout:
/// "a", "a and b", "a, b and c". Written once because a list that reads correctly only on a machine
/// with one of something is found only by driving the real window, and every list should be right or
/// wrong the same way.
/// </summary>
public static class Listed
{
    /// <summary>"a, b and c".</summary>
    public static string And(IReadOnlyList<string> names) => Joined(names, "and");

    /// <summary>"a, b or c".</summary>
    public static string Or(IReadOnlyList<string> names) => Joined(names, "or");

    private static string Joined(IReadOnlyList<string> names, string conjunction) => names.Count switch
    {
        0 => string.Empty,
        1 => names[0],
        _ => $"{string.Join(", ", names.Take(names.Count - 1))} {conjunction} {names[^1]}",
    };
}
