using System.Runtime.InteropServices;
using System.Security;
using Deguffer.Core.Safety;
using Microsoft.Win32;
using Windows.Management.Deployment;

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
/// <para><b>The list is documented, and the state is not.</b> The packages come from
/// <c>PackageManager.FindPackagesForUser</c> and the tasks from each one's manifest, both documented
/// (§7.5). The state is the <c>State</c> value Windows keeps under this user's <c>SystemAppData</c>
/// key for the package and the task, whose numbers are <c>StartupTaskState</c>'s documented ones and
/// whose location is not, so it is only ever read here, and a number outside the enumeration reads as
/// unknown.</para>
/// </remarks>
public sealed class WindowsPackagedStartupTasks : IPackagedStartupTasks
{
    public static WindowsPackagedStartupTasks Default { get; } = new();

    private const string StateKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private WindowsPackagedStartupTasks()
    {
    }

    public PackagedTasksRead Read(CancellationToken ct)
    {
        IEnumerable<Windows.ApplicationModel.Package> packages;

        try
        {
            // The empty user is the calling user, which is the one whose tasks start at its sign-in.
            packages = new PackageManager().FindPackagesForUserWithPackageTypes(string.Empty, PackageTypes.Main);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            return PackagedTasksRead.Unreadable;
        }

        var tasks = new List<PackagedTask>();
        var complete = true;

        using var states = Registry.CurrentUser.OpenSubKey(StateKey);

        foreach (var package in packages)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (FolderOf(package) is not { } folder)
                {
                    continue;
                }

                var manifest = Path.Combine(folder, "AppxManifest.xml");

                if (LongPath.ProbeFile(manifest) is not PathPresence.Present)
                {
                    continue;
                }

                var declared = StartupTaskManifest.Read(File.ReadAllText(LongPath.Extended(manifest)));

                if (declared.Count == 0)
                {
                    continue;
                }

                var family = package.Id.FamilyName;

                foreach (var task in declared)
                {
                    tasks.Add(new PackagedTask(
                        family,
                        task.TaskId,
                        task.DisplayName ?? Text(package.DisplayName) ?? package.Id.Name,
                        Text(package.PublisherDisplayName),
                        task.Executable is { } executable ? Path.GetFullPath(Path.Combine(folder, executable)) : null,
                        StateOf(states, family, task.TaskId)));
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
    /// The package's folder, or null where Windows says it is not there: a package registered from a
    /// folder that has since been deleted is still listed, and with no manifest it starts nothing, so
    /// it is not a gap in the list.
    /// </summary>
    private static string? FolderOf(Windows.ApplicationModel.Package package)
    {
        try
        {
            return package.InstalledLocation.Path;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static int? StateOf(RegistryKey? states, string family, string taskId)
    {
        try
        {
            using var task = states?.OpenSubKey(family + @"\" + taskId);

            return task?.GetValue("State") is int number && task.GetValueKind("State") is RegistryValueKind.DWord ? number : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
