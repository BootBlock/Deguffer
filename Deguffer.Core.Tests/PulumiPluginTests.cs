using Deguffer.Core.Providers;

namespace Deguffer.Core.Tests;

/// <summary>
/// The name rule for Pulumi's plugin folders. What is worth proving is that a folder is recognised only
/// where the version in its name is the text <c>pulumi plugin rm</c> can be given and mean that folder
/// alone, and that a second folder Pulumi would read as the same plugin is noticed.
/// </summary>
public sealed class PulumiPluginTests
{
    private const string Cache = @"C:\Users\testuser\.pulumi\plugins";

    [Theory]
    [InlineData("resource-aws-v6.0.0", "resource", "aws", "6.0.0")]
    [InlineData("resource-aws-native-v1.12.0", "resource", "aws-native", "1.12.0")]
    [InlineData("language-nodejs-v3.120.0-alpha.1", "language", "nodejs", "3.120.0-alpha.1")]
    [InlineData("tool-esc-v0.9.1+abc123", "tool", "esc", "0.9.1+abc123")]
    [InlineData("analyzer-policy-v1.0.0", "analyzer", "policy", "1.0.0")]
    [InlineData("converter-terraform-v1.0.20", "converter", "terraform", "1.0.20")]
    [InlineData("resource-github.com_org_repo-v0.1.0", "resource", "github.com_org_repo", "0.1.0")]
    public void RecognisesAFolderAsPulumiNamesIt(string name, string kind, string plugin, string version)
    {
        var recognised = PulumiPlugin.Recognise(Path.Combine(Cache, name));

        Assert.NotNull(recognised);
        Assert.Equal((kind, plugin, version), (recognised.Kind, recognised.Name, recognised.Version));
        Assert.Equal($"plugin rm {kind} {plugin} {version} --yes --non-interactive", recognised.RemoveArguments);
    }

    /// <summary>
    /// §5.2: a kind Pulumi does not know, a capital where Pulumi's rule is lower case, a version Pulumi
    /// would read leniently, one the command's range parser would take as a wildcard, an older Pulumi's
    /// extraction folder, and a name with no version are all Tier 4.
    /// </summary>
    [Theory]
    [InlineData("widget-aws-v6.0.0")]
    [InlineData("Resource-aws-v6.0.0")]
    [InlineData("resource-aws-v6.0")]
    [InlineData("resource-aws-v06.0.0")]
    [InlineData("resource-aws-vv6.0.0")]
    [InlineData("resource-aws-v6.0.0+windows-x64")]
    [InlineData("resource-aws-v6.0.0.tmp123456")]
    [InlineData("resource-aws-v99999999999999999999.0.0")]
    [InlineData("resource-aws")]
    [InlineData("credentials.json")]
    [InlineData("resource--v6.0.0")]
    public void RecognisesNothingElse(string name) =>
        Assert.Null(PulumiPlugin.Recognise(Path.Combine(Cache, name)));

    /// <summary>
    /// Pulumi's remove compares versions without their build metadata, reads a version leniently, and
    /// compares names exactly, so these are the folders its command for 6.0.0 would also take.
    /// </summary>
    [Theory]
    [InlineData("resource-aws-v6.0.0+build.1", true)]
    [InlineData("resource-aws-v6.0", true)]
    [InlineData("resource-aws-v6.0.1", false)]
    [InlineData("resource-aws-v6.0.0-beta.1", false)]
    [InlineData("resource-azure-v6.0.0", false)]
    [InlineData("language-aws-v6.0.0", false)]
    [InlineData("resource-AWS-v6.0.0", false)]
    [InlineData("resource-aws-v6.0.0", false)]
    [InlineData("resource-aws-v6.0.0.tmp123", false)]
    public void KnowsWhichFoldersItsRemoveWouldAlsoTake(string beside, bool alsoRemoved)
    {
        var plugin = PulumiPlugin.Recognise(Path.Combine(Cache, "resource-aws-v6.0.0"))!;

        Assert.Equal(alsoRemoved, plugin.CouldAlsoRemove(beside));
    }
}
