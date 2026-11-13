namespace Deguffer.Core.Text;

/// <summary>
/// A count with its noun, so every count reads the same way: grouped in the reader's culture, and in
/// the singular only for one. Both forms are given, because an English plural is not always an added "s".
/// </summary>
public static class Counted
{
    public static string Of(UInt128 count, string one, string many) => $"{count.ToString("N0", null)} {(count == 1 ? one : many)}";
}
