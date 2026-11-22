using System.Numerics;

namespace Deguffer.Core.Text;

/// <summary>
/// A count with its noun, so every count reads the same way: grouped in the reader's culture, and in
/// the singular only for one. Both forms are given, because an English plural is not always an added "s".
/// </summary>
public static class Counted
{
    /// <summary>"1 copy", "0 copies" or "1,234 copies".</summary>
    public static string Of<T>(T count, string one, string many)
        where T : IBinaryInteger<T> =>
        $"{count.ToString("N0", null)} {Form(count, one, many)}";

    /// <summary>
    /// The word or phrase that agrees with <paramref name="count"/> where the count is not written beside
    /// it: "is" or "are", "it" or "them", or a sentence whose singular words the number differently.
    /// </summary>
    public static string Form<T>(T count, string one, string many)
        where T : IBinaryInteger<T> =>
        count == T.One ? one : many;
}
