using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// A tier's name is <see cref="SafetyTierExtensions.ToDisplayName"/>'s, everywhere it is shown. A
/// literal copy in markup survives a rename of the tier, and then one page calls a tier something the
/// Storage rows and dialogs do not, so markup binds the name and no attribute may spell one out.
/// </summary>
public sealed class DesignTierNameTests
{
    [Fact]
    public void NoMarkupWritesATierNameAsALiteral()
    {
        var names = Enum.GetValues<SafetyTier>().Select(tier => tier.ToDisplayName()).ToHashSet(StringComparer.Ordinal);

        var found = ShellMarkup.Files.SelectMany(file => file.Elements
            .SelectMany(element => element.Attributes())
            .Where(attribute => names.Contains(attribute.Value))
            .Select(attribute => $"{file.Name}:{ShellMarkup.LineOf(attribute)} writes the tier name \"{attribute.Value}\" as a literal"));

        Assert.Empty(found);
    }
}
