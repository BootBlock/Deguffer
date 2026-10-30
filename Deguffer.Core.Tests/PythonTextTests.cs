using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// Python's <c>str.splitlines</c> and <c>str.strip</c>, which Conan's configuration and its
/// <c>.conanrc</c> are read by, so a line Deguffer counts or trims is the line Conan does.
/// </summary>
public sealed class PythonTextTests
{
    private static readonly string LineSeparator = ((char)0x2028).ToString();

    private static readonly string ParagraphSeparator = ((char)0x2029).ToString();

    public static TheoryData<string, string[]> Splits => new()
    {
        { "a\r\nb\r\nc\r\n", ["a", "b", "c"] },
        { "a\rb\nc", ["a", "b", "c"] },
        { "a\n\nb", ["a", "", "b"] },
        { "a\r\rb", ["a", "", "b"] },
        { "a\v\fb\x001cc\x001dd\x001ee", ["a", "", "b", "c", "d", "e"] },
        { "a\x0085b" + LineSeparator + "c" + ParagraphSeparator, ["a", "b", "c"] },
        { "a\x001fb", ["a\x001fb"] },
        { "", [] },
        { "\n", [""] },
    };

    [Theory]
    [MemberData(nameof(Splits))]
    public void SplitsLinesAsPythonDoes(string text, string[] lines) =>
        Assert.Equal(lines, PythonText.Lines(text));

    [Fact]
    public void StripsTheInformationSeparatorsAsPythonDoes() =>
        Assert.Equal("a b", PythonText.Strip("\x001c\x001f a b\x0085" + LineSeparator));
}
