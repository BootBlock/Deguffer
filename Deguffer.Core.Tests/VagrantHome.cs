using System.Text.Json;

namespace Deguffer.Core.Tests;

/// <summary>
/// A Vagrant home as Vagrant lays it out, for the tests of its prune: boxes, the dry run's report and
/// the machine index.
/// </summary>
internal static class VagrantHome
{
    public static string BoxesIn(string home) => Path.Combine(home, "boxes");

    /// <summary>
    /// One installed box, as <c>BoxCollection#add</c> lays it out: a provider folder holding
    /// <c>metadata.json</c> and the image, under an architecture folder where one is given.
    /// </summary>
    public static string Install(string home, string folder, string version, string provider, string? architecture = null, int bytes = 4096)
    {
        var path = architecture is null
            ? Path.Combine(BoxesIn(home), folder, version, provider)
            : Path.Combine(BoxesIn(home), folder, version, architecture, provider);

        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "metadata.json"), $$"""{"provider":"{{provider}}"}""");
        File.WriteAllBytes(Path.Combine(path, "box-disk001.vmdk"), new byte[bytes]);
        return path;
    }

    /// <summary>What prune's dry run prints for the given kept and removed lines.</summary>
    public static string Report(string[] kept, string[] removed) =>
        string.Join('\n', [
            "The following boxes will be kept...",
            .. kept,
            string.Empty,
            "Checking for older boxes...",
            .. removed.Length > 0 ? removed.Select(line => $"Would remove {line}") : ["No old versions of boxes to remove..."],
        ]) + "\n";

    /// <summary>bionic64 at 1.0.282 with 1.0.0 and 1.0.1 behind it, on VirtualBox.</summary>
    public static (string Kept, string[] Removed) InstallBionic(string home)
    {
        var kept = Install(home, "hashicorp-VAGRANTSLASH-bionic64", "1.0.282", "virtualbox", bytes: 8192);
        string[] removed =
        [
            Install(home, "hashicorp-VAGRANTSLASH-bionic64", "1.0.0", "virtualbox", bytes: 1000),
            Install(home, "hashicorp-VAGRANTSLASH-bionic64", "1.0.1", "virtualbox", bytes: 2000),
        ];

        return (kept, removed);
    }

    public static readonly string BionicReport = Report(
        ["hashicorp/bionic64 (virtualbox, 1.0.282)"],
        ["hashicorp/bionic64 virtualbox 1.0.0", "hashicorp/bionic64 virtualbox 1.0.1"]);

    /// <summary>
    /// The machine index as <c>MachineIndex#unlocked_save</c> writes it, one machine per box version
    /// given, with the architecture <c>Machine#id=</c> records for a Vagrantfile that names none.
    /// </summary>
    public static void WriteIndex(string home, params (string Machine, string Name, string Provider, string Version)[] machines)
    {
        var folder = Path.Combine(home, "data", "machine-index");
        Directory.CreateDirectory(folder);

        var index = new
        {
            version = 1,
            machines = machines.ToDictionary(
                machine => Guid.NewGuid().ToString("N"),
                machine => new
                {
                    name = machine.Machine,
                    provider = machine.Provider,
                    vagrantfile_path = @"C:\Users\testuser\projects\lab",
                    extra_data = new
                    {
                        box = new { name = machine.Name, provider = machine.Provider, architecture = "auto", version = machine.Version },
                    },
                }),
        };

        File.WriteAllText(Path.Combine(folder, "index"), JsonSerializer.Serialize(index));
    }
}
