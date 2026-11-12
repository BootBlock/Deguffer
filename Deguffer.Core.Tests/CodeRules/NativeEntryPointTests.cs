namespace Deguffer.Core.Tests.CodeRules;

/// <summary>
/// Each Win32 call is declared in one file, so its signature, its flags and the meaning of its errors
/// have one authority.
///
/// <para>Two declarations of one call drift apart, in the struct layout or the parameter shape they
/// give it. The calls already declared in more than one file are listed in
/// <c>Baselines/native-entry-points.txt</c> with how many files declare each. The number only falls;
/// a new call, or a new copy, is declared in the native class for its library and nowhere else.</para>
/// </summary>
public sealed class NativeEntryPointTests
{
    internal const string BaselineName = "native-entry-points";

    [Fact]
    public void NoWin32CallGainsASecondDeclaration()
    {
        var found = ProductSources.Win32Imports()
            .Select(call => (Name: call.Key, Files: call.Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .Where(call => call.Files.Count > 1)
            .SelectMany(call => call.Files.Select(file => new RuleViolation(call.Name, 0, $"declared in {file}")));

        RuleBaseline.Hold(
            $"{BaselineName}. Call the one declaration that exists, or move the declarations into one native "
                + $"class. The baseline is Baselines/{BaselineName}.txt.",
            found,
            Baseline.Read(BaselineName));
    }
}
