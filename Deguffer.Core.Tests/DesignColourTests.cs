using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// No colour is written as a literal outside a theme dictionary. A fixed colour is right in one theme
/// and wrong in the others: it cannot follow light, dark or high contrast, and it is drawn over a
/// backdrop the app does not control (§6.5). Every colour the shell draws is a theme resource or a
/// style, and the shell had no literal colour when this rule was written, so it holds with no
/// exceptions.
///
/// <para>Fluent's colour guidance: colours come from theme resources, never literals.
/// <c>Transparent</c> is not a colour in this sense: it is how an element takes pointer input with no
/// fill.</para>
/// </summary>
public sealed partial class DesignColourTests
{
    [Fact]
    public void NoColourIsWrittenOutsideAThemeDictionary()
    {
        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .Where(element => !element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "ResourceDictionary.ThemeDictionaries"))
            .SelectMany(element => element.Attributes().Select(attribute => attribute.Value)
                .Concat(element.HasElements ? [] : [element.Value])
                .Where(value => HexColour().IsMatch(value))
                .Select(value => file.At(element, $"<{element.Name.LocalName}> writes the colour {value.Trim()}"))));

        DesignBaseline.Hold("no literal colour outside a theme dictionary", found, new Dictionary<string, int>());
    }

    [GeneratedRegex(@"^\s*#(?:[0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\s*$")]
    private static partial Regex HexColour();
}
