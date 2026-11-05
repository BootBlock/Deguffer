namespace Deguffer.Core.Startup;

/// <summary>
/// What Windows says about the packages installed for this user, behind a seam so the reading and
/// its cache are proved against packages a test describes (G1).
/// </summary>
internal interface IInstalledPackages
{
    /// <summary>Every main package installed for the calling user.</summary>
    /// <exception cref="System.Runtime.InteropServices.COMException">Windows would not list them.</exception>
    /// <exception cref="UnauthorizedAccessException">This account may not list them.</exception>
    IReadOnlyList<IInstalledPackage> List();

    /// <summary>The whole text of the manifest at <paramref name="path"/>.</summary>
    string ReadManifest(string path);

    /// <summary>The startup task states Windows keeps for this user, open for one reading.</summary>
    IStartupTaskStates OpenStates();
}

/// <summary>One installed package. Only its identity is read up front, because the rest costs a call into Windows.</summary>
internal interface IInstalledPackage
{
    /// <summary>The name that includes the version, under which the package's files never change.</summary>
    string FullName { get; }

    string FamilyName { get; }

    string Name { get; }

    /// <summary>Whether it is registered from a developer's own folder, whose files can change in place.</summary>
    bool IsDevelopmentMode { get; }

    string? DisplayName { get; }

    string? PublisherDisplayName { get; }

    /// <summary>The package's folder, or null where Windows says it is not there.</summary>
    string? Folder();
}

/// <summary>The <c>StartupTaskState</c> numbers Windows recorded for this user.</summary>
internal interface IStartupTaskStates : IDisposable
{
    /// <summary>The number recorded for the task, or null where none could be read.</summary>
    int? StateOf(string family, string taskId);
}
