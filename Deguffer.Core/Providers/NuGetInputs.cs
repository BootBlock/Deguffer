using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Everything <c>dotnet nuget locals all --list</c> reads to say where NuGet keeps its caches, so
/// <see cref="ToolAnswerMemory"/> asks again the moment any of it changes (#1059).
///
/// <para><b>Configuration.</b> NuGet merges a <c>NuGet.Config</c> from the folder it runs in and every
/// folder above it, the user's <c>%APPDATA%\NuGet\NuGet.Config</c>, every <c>*.config</c> in
/// <c>%APPDATA%\NuGet\config</c>, and every <c>*.config</c> in
/// <c>%ProgramFiles(x86)%\NuGet\Config</c> (<c>Settings.LoadDefaultSettings</c>), so a
/// <c>globalPackagesFolder</c> in any of them moves the packages folder. NuGet takes the name
/// case-insensitively on Windows, so one spelling stamps every one.</para>
///
/// <para><b>Variables.</b> <c>NUGET_PACKAGES</c>, <c>NUGET_HTTP_CACHE_PATH</c>,
/// <c>NUGET_PLUGINS_CACHE_PATH</c>, <c>NUGET_SCRATCH</c> and <c>TMP</c> or <c>TEMP</c> each move a
/// location, and the whole block is in the fingerprint, so none needs an entry.</para>
///
/// <para><b>Code.</b> The <c>dotnet</c> host, the <c>global.json</c> it looks for from the folder it
/// runs in up to the drive root, and the listing of the SDKs beside it: between them they choose the
/// SDK, and so the NuGet, that answers.</para>
/// </summary>
internal static class NuGetInputs
{
    private static readonly string[] ProjectFiles = ["global.json", "NuGet.Config"];

    /// <param name="dotnet">The <c>dotnet</c> host asked.</param>
    /// <param name="environment">The account whose roaming profile holds the user's NuGet configuration.</param>
    /// <param name="system">Where the machine-wide NuGet configuration is.</param>
    public static ToolInputs Of(string dotnet, IUserEnvironment environment, ISystemDirectories system)
    {
        var userSettings = Path.Combine(environment.RoamingAppData, "NuGet");

        List<string> files =
        [
            dotnet,
            .. ToolInputs.InEachFolderUp(ToolInputs.WorkingDirectory(environment), ProjectFiles),
            Path.Combine(userSettings, "NuGet.Config"),
        ];

        List<ListedFolder> folders =
        [
            new(Path.Combine(userSettings, "config")),
            new(Path.Combine(system.ProgramFilesX86, "NuGet", "Config")),
        ];

        if (Path.GetDirectoryName(dotnet) is { } host)
        {
            folders.Add(new ListedFolder(Path.Combine(host, "sdk")));
        }

        return new ToolInputs { Files = files, Folders = folders };
    }
}
