namespace Deguffer.Core.Tests.CodeRules;

/// <summary>
/// Each Win32 call is declared in one file, so its signature, its flags and the meaning of its errors
/// have one authority.
///
/// <para>Two declarations of one call drift: Deguffer once held two layouts of
/// <c>WIN32_FIND_DATAW</c> and two shapes of <c>NtCreateFile</c>. The calls declared in more than one
/// file when this rule came in are listed in <c>Baselines/native-entry-points.txt</c> with how many
/// files declare each. The number only falls; a new call, or a new copy, is declared in the native
/// class for its library and nowhere else.</para>
/// </summary>
public sealed class NativeEntryPointTests
{
    internal const string BaselineName = "native-entry-points";

    [Fact]
    public void NoWin32CallGainsASecondDeclaration()
    {
        var found = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var call in ProductSources.Win32Imports())
        {
            var files = call.Distinct(StringComparer.OrdinalIgnoreCase).Count();

            if (files > 1)
            {
                found[call.Key] = files;
            }
        }

        var expected = Baseline.Read(BaselineName);

        Assert.True(
            expected.SequenceEqual(found),
            Baseline.Difference(
                BaselineName,
                expected,
                found,
                "Call the one declaration that exists, or move the declarations into one native class."));
    }
}
