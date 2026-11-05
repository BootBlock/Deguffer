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

    /// <summary>Only the element names must be written literally, so the category is read as XML reads it.</summary>
    [Fact]
    public void ACategoryWrittenWithCharacterReferencesIsStillAStartupTask() =>
        Assert.Equal(
            [new DeclaredStartupTask("ToolStartup", "Tool", @"Tool\Tool.exe")],
            StartupTaskManifest.Read("""
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                         xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10">
                  <Application Executable="Tool\Tool.exe">
                    <desktop:Extension Category="&#119;indows.&#x73;tartup&#84;ask">
                      <desktop:StartupTask TaskId="ToolStartup" DisplayName="Tool" />
                    </desktop:Extension>
                  </Application>
                </Package>
                """));

    /// <summary>An extension takes the program of the application it is inside, and none outside one.</summary>
    [Fact]
    public void EachExtensionTakesTheProgramOfItsOwnApplication() =>
        Assert.Equal(
            [
                new DeclaredStartupTask("First", null, @"First\First.exe"),
                new DeclaredStartupTask("Second", null, @"Second\Second.exe"),
                new DeclaredStartupTask("Outside", null, null),
            ],
            StartupTaskManifest.Read("""
                <Package xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5">
                  <Applications>
                    <Application Id="First" Executable="First\First.exe">
                      <uap5:Extension Category="windows.startupTask"><uap5:StartupTask TaskId="First" /></uap5:Extension>
                    </Application>
                    <Application Id="Empty" Executable="Empty\Empty.exe" />
                    <Application Id="Second" Executable="Second\Second.exe">
                      <Extensions>
                        <uap5:Extension Category="windows.startupTask"><uap5:StartupTask TaskId="Second" /></uap5:Extension>
                      </Extensions>
                    </Application>
                  </Applications>
                  <uap5:Extension Category="windows.startupTask"><uap5:StartupTask TaskId="Outside" /></uap5:Extension>
                </Package>
                """));

    /// <summary>A task read before the fault does not make a broken manifest a partial one.</summary>
    [Fact]
    public void AManifestThatBreaksAfterItsTaskDeclaresNothing() =>
        Assert.Empty(StartupTaskManifest.Read("""
            <Package xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5">
              <Application Executable="Tool.exe">
                <uap5:Extension Category="windows.startupTask"><uap5:StartupTask TaskId="T" /></uap5:Extension>
              </Application>
              <Applications>
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
