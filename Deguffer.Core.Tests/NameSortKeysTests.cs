using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>
/// <see cref="NameSortKeys"/> reaches the order <see cref="ExploreTree.CompareByName(ReadOnlySpan{char}, int, ReadOnlySpan{char}, int)"/>
/// states without reading most names. A key that disagreed with it would draw equal siblings in a
/// different order on the map than the list shows, and move them between two scans of one disk.
/// </summary>
public class NameSortKeysTests
{
    /// <summary>
    /// Names chosen where a key could disagree with the names: letters either side of the gap between
    /// <c>Z</c> and <c>a</c>, which upper-casing moves; characters past ASCII whose capitals are ASCII
    /// letters elsewhere (<c>ſ</c>, <c>ı</c>, the Kelvin sign); a character outside the basic plane;
    /// names that tie for sixteen characters; names that go on past a character beyond ASCII; and
    /// names that end where another goes on.
    /// </summary>
    private static readonly string[] Awkward =
    [
        "", "a", "A", "ab", "aB", "Ab", "a_", "a[", "a`", "a{", "a~", "Z", "z", "_", "`", "[", "~",
        "s", "S", "\u017F", "\u017Fa", "i", "I", "\u0131", "k", "K", "\u212A", "e", "\u00E9", "\u00C9", "f",
        "\U00010400", "\U00010428", "a\U00010400", "a\u00E9", "a\u00E9z", "a\u00F1b", "aa",
        "abcdefghijklmno", "abcdefghijklmnop", "ABCDEFGHIJKLMNOP", "abcdefghijklmnopq", "abcdefghijklmnoq",
        "abcdefghijklmno\u00E9", "abcdefghijklmnop\u00E9", "amd64_microsoft-windows-a.manifest",
        "AMD64_Microsoft-Windows-b.manifest", "amd64_microsoft-windows-\u00E9.manifest",
    ];

    /// <summary>
    /// What every key rests on, held against the runtime rather than assumed: compared ordinally
    /// without regard to case, no character past ASCII equals an ASCII one or comes before it, and
    /// ASCII characters order as their capitals do. A runtime that folded <c>ſ</c> to <c>S</c> would
    /// fail here rather than reorder siblings.
    /// </summary>
    [Fact]
    public void OrdinalComparisonWithoutCaseKeepsAsciiApartFromTheRest()
    {
        Span<char> past = stackalloc char[1];
        Span<char> ascii = stackalloc char[1];

        for (var c = 0x80; c <= 0xFFFF; c++)
        {
            past[0] = (char)c;

            for (var a = 1; a < 0x80; a++)
            {
                ascii[0] = (char)a;

                if (past.CompareTo(ascii, StringComparison.OrdinalIgnoreCase) <= 0)
                {
                    Assert.Fail($"U+{c:X4} does not order after U+{a:X4}.");
                }
            }
        }

        Span<char> other = stackalloc char[1];

        for (var a = 1; a < 0x80; a++)
        {
            ascii[0] = (char)a;

            for (var b = 1; b < 0x80; b++)
            {
                other[0] = (char)b;

                Assert.Equal(
                    Math.Sign(char.ToUpperInvariant((char)a) - char.ToUpperInvariant((char)b)),
                    Math.Sign(ascii.CompareTo(other, StringComparison.OrdinalIgnoreCase)));
            }
        }
    }

    [Fact]
    public void AKeyOrdersAsItsNameDoesWhereverTwoKeysDiffer()
    {
        foreach (var left in Awkward)
        {
            foreach (var right in Awkward)
            {
                var byKey = NameSortKeys.KeyOf(left).CompareTo(NameSortKeys.KeyOf(right));
                var byName = left.AsSpan().CompareTo(right, StringComparison.OrdinalIgnoreCase);

                if (byKey != 0)
                {
                    Assert.True(Math.Sign(byKey) == Math.Sign(byName), $"\"{left}\" and \"{right}\" order differently by key.");
                }
            }
        }
    }

    /// <summary>
    /// Keys tie for names alike in their first sixteen characters, and for names alike up to a
    /// character past ASCII, and those are compared whole. Names that differ only in case tie by key
    /// and by name, and are put in node order.
    /// </summary>
    [Fact]
    public void SiblingsAreInTheOrderTheirNamesComparedWholePutThemIn()
    {
        var pool = new NamePool();

        // Each name twice, as two nodes, so equal names are ordered by node; and in an order neither
        // sorted nor reversed, so only the sort can put them in order.
        var names = Awkward.Concat(Awkward).ToArray();
        var nameNumbers = names.Select(name => pool.Intern(name)).ToArray();
        var frozen = pool.Freeze();
        var siblings = Enumerable.Range(0, names.Length).Select(i => i * 37 % names.Length).ToArray();

        Assert.Equal(names.Length, siblings.Distinct().Count());

        var expected = siblings.ToArray();
        Array.Sort(expected, (left, right) => ExploreTree.CompareByName(nameNumbers, frozen, left, right));

        NameSortKeys.Of(nameNumbers, frozen, workers: 2, CancellationToken.None)
            .Order(siblings, new UInt128[siblings.Length]);

        Assert.Equal(expected, siblings);
    }
}
