using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One folder at the same place inside every packaged application's container, found without looking
/// through a link.
///
/// <para><b>Why a container holds a vendor's folder at all.</b> Windows runs a packaged application
/// in an AppContainer, and gives it <c>Packages\&lt;package&gt;\AC</c> in place of the local
/// application data. A driver writing on behalf of that application writes there, so the folder it
/// keeps under <c>%LOCALAPPDATA%</c> reappears once inside each container that has used it.</para>
///
/// <para><b>Every segment is checked, because the path is built rather than listed.</b> The package
/// folders come from a listing, which sets each link aside. The container and the folder inside it
/// are joined from names, so a junction at either would put every deletion on the far side while each
/// §5.6 survivor resolved through the same link and passed.</para>
///
/// <para><b>An obstacle is recorded only where something recognised may be behind it.</b> Every
/// installed package has a container, and a container moved elsewhere is not a sentence about shader
/// caches unless one is there. So a recognised name is probed through the obstacle. That probe
/// decides whether to say something, never whether to look inside, which is the rule
/// <see cref="ChromiumUserDataDiscovery"/> applies to a declared host's folder.</para>
/// </summary>
internal static class PackageContainers
{
    /// <summary>The folder of <c>%LOCALAPPDATA%</c> that holds each packaged application's data.</summary>
    public const string PackagesFolderName = "Packages";

    /// <summary>The folder inside a package that an AppContainer is given as its local application data.</summary>
    public const string ContainerFolderName = "AC";

    /// <summary>
    /// <paramref name="folderName"/> inside each package's container under
    /// <paramref name="environment"/>'s local application data.
    /// </summary>
    /// <param name="recognised">
    /// The children of that folder its caller would act on. Probed through an obstacle only, to decide
    /// whether the obstacle is worth naming.
    /// </param>
    public static PackageContainerSearch Find(
        IUserEnvironment environment,
        string folderName,
        IReadOnlyList<string> recognised,
        CancellationToken ct)
    {
        var packages = Path.Combine(environment.LocalAppData, PackagesFolderName);

        switch (LongPath.ProbeDirectory(packages, out var packagesIsALink))
        {
            case PathPresence.Absent:
                return PackageContainerSearch.Nothing;

            case PathPresence.Refused:
                return new PackageContainerSearch([], [], Unlisted: packages);
        }

        // Windows creates this folder and nothing in it is a cache. One moved onto another drive is
        // not identified as holding any, so it is not looked through and not named.
        if (packagesIsALink is true)
        {
            return PackageContainerSearch.Nothing;
        }

        var scan = ChildDirectories.Under(packages);

        if (scan.Unreadable)
        {
            return new PackageContainerSearch([], [], Unlisted: packages);
        }

        var found = new List<PackageContainerFolder>();
        var obstructed = new List<DerivedPathObstacle>();

        // A linked package folder is set aside by the listing and not reported, for the reason a
        // linked Packages folder is not: nothing has identified a cache behind it.
        foreach (var package in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var packagePath = LongPath.Display(package.FullName);
            var container = Path.Combine(packagePath, ContainerFolderName);
            var folder = Path.Combine(container, folderName);

            if (DerivedPath.FirstObstacleBetween(packagePath, folder) is { } obstacle)
            {
                if (recognised.Any(name => LongPath.ProbeDirectory(Path.Combine(folder, name)) is not PathPresence.Absent))
                {
                    obstructed.Add(obstacle);
                }

                continue;
            }

            // Absence is all the walk leaves: it stops on a link and on a refusal, and the folder is
            // its last segment.
            if (LongPath.DirectoryExists(folder))
            {
                found.Add(new PackageContainerFolder(package.Name, container, folder));
            }
        }

        return new PackageContainerSearch(found, obstructed, Unlisted: null);
    }
}

/// <summary>What <see cref="PackageContainers.Find"/> found.</summary>
/// <param name="Found">Each package whose container holds the folder, reached without a link.</param>
/// <param name="Obstructed">
/// Each link or refused segment with a recognised name behind it, which the caller names rather than
/// looks through.
/// </param>
/// <param name="Unlisted">
/// The packages folder, where Windows would not list it or describe it, so no container was examined.
/// Null otherwise.
/// </param>
internal sealed record PackageContainerSearch(
    IReadOnlyList<PackageContainerFolder> Found,
    IReadOnlyList<DerivedPathObstacle> Obstructed,
    string? Unlisted)
{
    public static PackageContainerSearch Nothing { get; } = new([], [], Unlisted: null);
}

/// <summary>The folder inside one package's container.</summary>
/// <param name="Package">The package folder's name, which is the package family name.</param>
/// <param name="Container">The package's <c>AC</c> folder, which must survive whatever is removed from inside it.</param>
/// <param name="Path">The folder itself.</param>
internal readonly record struct PackageContainerFolder(string Package, string Container, string Path);
