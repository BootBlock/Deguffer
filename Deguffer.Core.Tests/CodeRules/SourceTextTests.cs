namespace Deguffer.Core.Tests.CodeRules;

/// <summary>
/// The masking the code rules read through: a rule about code never matches a comment or a literal
/// that names its breach, and line numbers survive.
/// </summary>
public sealed class SourceTextTests
{
    [Theory]
    [InlineData("var a = 1; // GetFiles(")]
    [InlineData("/* GetFiles(\n GetFiles( */ var a = 1;")]
    [InlineData("/// <c>GetFiles(</c>\nvar a = 1;")]
    [InlineData("var s = \"GetFiles(\";")]
    [InlineData("var s = \"a \\\" GetFiles(\";")]
    [InlineData("var s = @\"a \"\" GetFiles(\";")]
    [InlineData("var s = $\"{x} GetFiles(\";")]
    [InlineData("var s = \"\"\"\n GetFiles(\n \"\"\";")]
    [InlineData("var s = $$\"\"\"{{x}} GetFiles(\"\"\";")]
    [InlineData("var s = $\"{F(\"GetFiles(\")}\";")]
    [InlineData("var s = $@\"{F(\"GetFiles(\")} \"\" \";")]
    public void CodeBlanksCommentsAndLiterals(string source)
    {
        Assert.DoesNotContain("GetFiles(", SourceText.Mask(source, SourceView.Code), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Directory.GetFiles(path);")]
    [InlineData("var q = '\"'; Directory.GetFiles(path);")]
    [InlineData("var q = '\\''; Directory.GetFiles(path);")]
    [InlineData("var e = \"\"; Directory.GetFiles(path);")]
    [InlineData("var s = @\"a\\\"; Directory.GetFiles(path);")]
    [InlineData("var q = @\"\"\"\"; Directory.GetFiles(path);")]
    [InlineData("var q = $\"{F(\"x\")}\"; Directory.GetFiles(path);")]
    public void CodeKeepsTheCodeAfterALiteral(string source)
    {
        Assert.Contains("GetFiles(", SourceText.Mask(source, SourceView.Code), StringComparison.Ordinal);
    }

    [Fact]
    public void LiteralsSurviveWhereTheRuleReadsThem()
    {
        var masked = SourceText.Mask("Note(\"a sentence\"); // a sentence", SourceView.CodeAndLiterals);

        Assert.Equal("Note(\"a sentence\");", masked.TrimEnd());
    }

    [Fact]
    public void MaskingKeepsEveryLineBreakAndTheLength()
    {
        const string Source = "a /* x\r\n y */ b\n// c\n\"d\"";

        var masked = SourceText.Mask(Source, SourceView.Code);

        Assert.Equal(Source.Length, masked.Length);
        Assert.Equal(Source.Count(c => c == '\n'), masked.Count(c => c == '\n'));
        Assert.Equal(Source.Count(c => c == '\r'), masked.Count(c => c == '\r'));
    }
}
