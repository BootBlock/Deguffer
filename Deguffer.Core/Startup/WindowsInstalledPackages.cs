using System.Security;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Deguffer.Core.Startup;

/// <inheritdoc />
/// <remarks>
/// <para><b>The list is documented, and the state is not.</b> The packages come from
/// <c>PackageManager.FindPackagesForUser</c> and the tasks from each one's manifest, both documented
/// (§7.5). The state is the <c>State</c> value Windows keeps under this user's <c>SystemAppData</c>
/// key for the package and the task, whose numbers are <c>StartupTaskState</c>'s documented ones and
/// whose location is not, so it is only ever read here, and a number outside the enumeration reads as
/// unknown.</para>
/// </remarks>
internal sealed class WindowsInstalledPackages : IInstalledPackages
{
    public static WindowsInstalledPackages Default { get; } = new();

    private const string StateKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    private WindowsInstalledPackages()
    {
    }

    public IReadOnlyList<IInstalledPackage> List() =>
        // The empty user is the calling user, which is the one whose tasks start at its sign-in.
        // Listed in full here rather than as the caller walks it, because the list is fetched as it
        // is walked and a refusal part-way is a refusal of the list.
        [.. new PackageManager()
            .FindPackagesForUserWithPackageTypes(string.Empty, PackageTypes.Main)
            .Select(package => new InstalledPackage(package))];

    public string ReadManifest(string path) => File.ReadAllText(path);

    public IStartupTaskStates OpenStates() => new RegistryStates(OpenStateKey());

    private static RegistryKey? OpenStateKey()
    {
        try
        {
            return Registry.CurrentUser.OpenSubKey(StateKey);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // A key Windows would not open leaves every state unread, which each row shows as unknown.
            return null;
        }
    }

    private sealed class InstalledPackage(Package package) : IInstalledPackage
    {
        private readonly PackageId id = package.Id;

        public string FullName => id.FullName;

        public string FamilyName => id.FamilyName;

        public string Name => id.Name;

        public bool IsDevelopmentMode => package.IsDevelopmentMode;

        public string? DisplayName => package.DisplayName;

        public string? PublisherDisplayName => package.PublisherDisplayName;

        /// <remarks>
        /// <para>A package registered from a folder that has since been deleted is still listed, and
        /// with no manifest it starts nothing, so it is not a gap in the list.</para>
        /// <para><c>InstalledPath</c> gives the folder as text, where <c>InstalledLocation</c> builds a
        /// <c>StorageFolder</c> first, which measured at most of the cost of a first reading. Windows
        /// added it in 1903, and the projection declares it from 2004, the version guarded here, so
        /// older versions take the slower route.</para>
        /// </remarks>
        public string? Folder()
        {
            try
            {
                return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                    ? package.InstalledPath
                    : package.InstalledLocation.Path;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
        }
    }

    private sealed class RegistryStates(RegistryKey? states) : IStartupTaskStates
    {
        public int? StateOf(string family, string taskId)
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

        public void Dispose() => states?.Dispose();
    }
}
