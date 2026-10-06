using System.Text.Json;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// What Docker Desktop's settings say about where its data disk is.
/// </summary>
/// <param name="Disks">
/// The data disk the settings name, whether or not it is there. Empty where Docker Desktop is not
/// installed for this account, or its settings could not be read.
/// </param>
/// <param name="Problem">
/// Why the settings could not be read, for the user, or null where they were read or are absent.
/// </param>
public sealed record DockerDesktopSettingsReading(IReadOnlyList<VirtualDisk> Disks, string? Problem)
{
    public static DockerDesktopSettingsReading None { get; } = new([], null);
}

/// <summary>
/// Docker Desktop's data disk, found from Docker Desktop's own settings file.
///
/// <para>Docker documents where the disk is by default for each backend, and that its "Disk image
/// location" setting moves it. The file is <c>%APPDATA%\Docker\settings-store.json</c>, renamed from
/// <c>settings.json</c> in 4.35, so both are read and the newer wins. The documented key
/// <c>wslEngineEnabled</c> chooses the backend. Docker documents no key for the moved location: the one
/// read here is the one Docker Desktop has been seen to write, <c>CustomWslDistroDir</c> for the WSL 2
/// backend and <c>dataFolder</c> for Hyper-V, compared without regard to case because the older file
/// spelled them in camel case.</para>
///
/// <para>The WSL 2 backend's older disk, the <c>docker-desktop-data</c> distribution, is a WSL
/// registration and is found by <see cref="WslRegistrations"/>, never assumed here.</para>
/// </summary>
public static class DockerDesktopDisks
{
    /// <summary>Docker Desktop's settings are a few kilobytes. Anything larger is not them.</summary>
    private const int MaximumSettingsBytes = 1024 * 1024;

    /// <summary>The settings files, newest name first.</summary>
    private static readonly string[] SettingsFiles = ["settings-store.json", "settings.json"];

    public static DockerDesktopSettingsReading Read(IUserEnvironment environment, ISystemDirectories system)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(system);

        var folder = Path.Combine(environment.RoamingAppData, "Docker");

        foreach (var name in SettingsFiles)
        {
            var path = Path.Combine(folder, name);

            switch (LongPath.ProbeFile(path))
            {
                case PathPresence.Absent:
                    continue;

                case PathPresence.Refused:
                    return new([], $"Windows would not let Deguffer read Docker Desktop's settings, {path}, so "
                        + "where its data disk is could not be found.");
            }

            using var settings = BoundedJsonFile.Read(path, MaximumSettingsBytes);

            return settings is null
                ? new([], $"Docker Desktop's settings, {path}, could not be read, so where its data disk is "
                    + "could not be found.")
                : new(Disks(settings.RootElement, environment, system), null);
        }

        return DockerDesktopSettingsReading.None;
    }

    private static IReadOnlyList<VirtualDisk> Disks(
        JsonElement settings, IUserEnvironment environment, ISystemDirectories system)
    {
        // The WSL 2 backend is Docker Desktop's default on Windows, so a file that does not say
        // otherwise is read as using it.
        if (Property(settings, "wslEngineEnabled") is not { ValueKind: JsonValueKind.False })
        {
            var root = Folder(settings, "CustomWslDistroDir") ?? Path.Combine(environment.LocalAppData, "Docker", "wsl");

            return [new VirtualDisk(Path.Combine(root, "disk", "docker_data.vhdx"), VirtualDiskKind.DockerData, null)];
        }

        var data = Folder(settings, "dataFolder") ?? Path.Combine(system.ProgramData, "DockerDesktop", "vm-data");

        return [new VirtualDisk(Path.Combine(data, "DockerDesktop.vhdx"), VirtualDiskKind.DockerData, null)];
    }

    private static string? Folder(JsonElement settings, string name) =>
        Property(settings, name) is { ValueKind: JsonValueKind.String } value
            ? LongPath.Configured(value.GetString())
            : null;

    private static JsonElement? Property(JsonElement settings, string name)
    {
        foreach (var property in settings.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
