using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Startup;

/// <summary>One packaged application's startup task, as the page lists it.</summary>
/// <param name="PackageFamilyName">The family Windows keeps the task's state under.</param>
/// <param name="Name">The task's own name, or its package's where the manifest gives none Deguffer can read.</param>
/// <param name="Publisher">The package's publisher.</param>
/// <param name="Program">The program it starts, as a full path inside the package, or null where the manifest names none.</param>
/// <param name="State">The <c>StartupTaskState</c> number Windows recorded for this user, or null where it recorded none that could be read.</param>
public sealed record PackagedTask(
    string PackageFamilyName,
    string TaskId,
    string Name,
    string? Publisher,
    string? Program,
    int? State);

/// <summary>Every packaged startup task, and whether the list of packages could be read at all.</summary>
public sealed record PackagedTasksRead(bool Complete, IReadOnlyList<PackagedTask> Tasks)
{
    public static PackagedTasksRead Unreadable { get; } = new(false, []);
}

/// <summary>
/// The startup tasks the packages installed for this user declare, behind a seam so the page is
/// proved against tasks a test describes (G1).
/// </summary>
public interface IPackagedStartupTasks
{
    PackagedTasksRead Read(CancellationToken ct);
}

/// <inheritdoc />
/// <remarks>
/// <para><b>A manifest is read once per package version.</b> A package's files are fixed for its full
/// name, which includes its version, and an update installs a new full name in a new folder, so the
/// tasks a manifest declares and the folder they are inside are kept for the life of the process. A
/// package registered in development mode is the exception, because its folder is the developer's
/// own and can change in place. The state is read at every reading, because that is what an enable
/// or disable changes.</para>
/// <para>A package whose folder or manifest is missing is not kept, so a package caught part-way
/// through being installed is read again next time rather than remembered as declaring nothing.</para>
/// </remarks>
public sealed class PackagedStartupTasks : IPackagedStartupTasks
{
    public static PackagedStartupTasks Default { get; } = new(WindowsInstalledPackages.Default);

    private const string ManifestName = "AppxManifest.xml";

    private readonly IInstalledPackages packages;

    /// <summary>The manifests already read, by package full name. The Memory and Startup pages read at once.</summary>
    private readonly ConcurrentDictionary<string, KnownManifest> known = new(StringComparer.Ordinal);

    internal PackagedStartupTasks(IInstalledPackages packages)
    {
        this.packages = packages;
    }

    public PackagedTasksRead Read(CancellationToken ct)
    {
        IReadOnlyList<IInstalledPackage> installed;

        try
        {
            installed = packages.List();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return PackagedTasksRead.Unreadable;
        }

        var tasks = new List<PackagedTask>();
        var complete = true;

        using var states = packages.OpenStates();

        foreach (var package in installed)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (ManifestOf(package) is not { Tasks.Count: > 0 } manifest)
                {
                    continue;
                }

                var family = package.FamilyName;

                foreach (var task in manifest.Tasks)
                {
                    tasks.Add(new PackagedTask(
                        family,
                        task.TaskId,
                        task.DisplayName ?? Text(package.DisplayName) ?? package.Name,
                        Text(package.PublisherDisplayName),
                        task.Executable is { } executable ? Path.GetFullPath(Path.Combine(manifest.Folder, executable)) : null,
                        states.StateOf(family, task.TaskId)));
                }
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or SecurityException or IOException or ArgumentException)
            {
                // A package being installed or removed as the list is read, or one whose folder this
                // account may not read. The rest are still listed, and the page says the list is short.
                complete = false;
            }
        }

        return new PackagedTasksRead(complete, tasks);
    }

    /// <summary>
    /// The package's folder and the tasks its manifest declares, or null where it has no manifest and
    /// so starts nothing.
    /// </summary>
    private KnownManifest? ManifestOf(IInstalledPackage package)
    {
        var fullName = package.FullName;
        var fixedFiles = !package.IsDevelopmentMode;

        if (fixedFiles && known.TryGetValue(fullName, out var seen))
        {
            return seen;
        }

        if (package.Folder() is not { } folder)
        {
            return null;
        }

        string text;

        try
        {
            text = packages.ReadManifest(LongPath.Extended(Path.Combine(folder, ManifestName)));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Opening the file answers whether it is there. Any other refusal is left to the caller,
            // which marks the list incomplete, because this package may declare a task the list lacks.
            return null;
        }

        var manifest = new KnownManifest(folder, StartupTaskManifest.Read(text));

        if (fixedFiles)
        {
            known[fullName] = manifest;
        }

        return manifest;
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record KnownManifest(string Folder, IReadOnlyList<DeclaredStartupTask> Tasks);
}
