namespace Deguffer.App.Shell;

/// <summary>
/// The components Deguffer redistributes, which About credits with their licences. Each build is
/// self-contained (§6.3), so the .NET runtime and the Windows App SDK ship inside it alongside the
/// referenced packages. A licence address is the one the package's own metadata gives.
/// </summary>
public static class ThirdPartyNotices
{
    private static readonly Uri Mit = new("https://licenses.nuget.org/MIT");

    public static readonly IReadOnlyList<ThirdPartyComponent> Components =
    [
        new(".NET", [], "MIT", new("https://github.com/dotnet/runtime/blob/main/LICENSE.TXT")),
        new("Windows App SDK", ["Microsoft.WindowsAppSDK"], "Microsoft Software License Terms", new("https://www.nuget.org/packages/Microsoft.WindowsAppSDK")),
        new("Win2D", ["Microsoft.Graphics.Win2D"], "Microsoft Software License Terms", new("https://www.microsoft.com/web/webpi/eula/eula_win2d_10012014.htm")),
        new("CommunityToolkit.Mvvm", ["CommunityToolkit.Mvvm"], "MIT", Mit),
        new("System.IO.Hashing", ["System.IO.Hashing"], "MIT", Mit),
    ];
}
