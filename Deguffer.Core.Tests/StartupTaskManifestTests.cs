using Deguffer.Core.Startup;

namespace Deguffer.Core.Tests;

/// <summary>
/// A packaged app's startup tasks come from its manifest (§7.5), in whichever of the two documented
/// namespaces it declares them. The manifests here are invented in the documented shape.
/// </summary>
public sealed class StartupTaskManifestTests
{
    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
                 xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10">
          <Applications>
            <Application Id="App" Executable="Tool\Tool.exe" EntryPoint="Windows.FullTrustApplication">
              <Extensions>
                <uap5:Extension Category="windows.startupTask">
                  <uap5:StartupTask TaskId="ToolStartup" Enabled="false" DisplayName="Tool" />
                </uap5:Extension>
                <desktop:Extension Category="windows.startupTask" Executable="Helper\Helper.exe" EntryPoint="Windows.FullTrustApplication">
                  <desktop:StartupTask TaskId="HelperStartup" Enabled="true" DisplayName="ms-resource:HelperName" />
                </desktop:Extension>
                <uap5:Extension Category="windows.appExecutionAlias">
                  <uap5:AppExecutionAlias />
                </uap5:Extension>
              </Extensions>
            </Application>
          </Applications>
        </Package>
        """;

    [Fact]
    public void EveryStartupTaskIsReadWithTheProgramItStarts()
    {
        var tasks = StartupTaskManifest.Read(Manifest);

        Assert.Equal(
            [
                new DeclaredStartupTask("ToolStartup", "Tool", @"Tool\Tool.exe"),
                new DeclaredStartupTask("HelperStartup", null, @"Helper\Helper.exe"),
            ],
            tasks);
    }

    [Fact]
    public void AManifestThatWillNotParseDeclaresNothing() =>
        Assert.Empty(StartupTaskManifest.Read("<Package><Applications>"));

    /// <summary>A manifest is data from a package, so a document type is refused rather than processed.</summary>
    [Fact]
    public void AManifestWithADocumentTypeDeclaresNothing() =>
        Assert.Empty(StartupTaskManifest.Read("""
            <?xml version="1.0"?>
            <!DOCTYPE Package [<!ENTITY x "windows.startupTask">]>
            <Package><Extension Category="&x;"><StartupTask TaskId="T" /></Extension></Package>
            """));

    [Theory]
    [InlineData(0, StartupState.Disabled)]
    [InlineData(1, StartupState.Disabled)]
    [InlineData(2, StartupState.Starts)]
    [InlineData(3, StartupState.Disabled)]
    [InlineData(4, StartupState.Starts)]
    [InlineData(5, StartupState.Unknown)]
    [InlineData(null, StartupState.Unknown)]
    public void TheStateIsStartupTaskStatesDocumentedMeaning(int? number, StartupState expected) =>
        Assert.Equal(expected, StartupTaskManifest.StateOf(number));
}
