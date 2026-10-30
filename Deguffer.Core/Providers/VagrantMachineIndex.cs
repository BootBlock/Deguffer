using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One machine Vagrant's index records, and the box version it was created from.
/// </summary>
/// <param name="Name">The machine's name in its Vagrantfile, such as <c>default</c>.</param>
/// <param name="Project">The folder holding that Vagrantfile, or null where the index records none.</param>
internal sealed record VagrantMachine(string Name, string? Project, VagrantBoxVersion Box);

/// <summary>
/// What Vagrant's machine index says about which box versions machines were created from.
/// </summary>
/// <param name="Machines">Every recorded machine that names its box.</param>
/// <param name="Unread">The index where it is there and could not be read, or null.</param>
internal sealed record VagrantMachineIndexReading(IReadOnlyList<VagrantMachine> Machines, UnreadFile? Unread)
{
    /// <summary>
    /// The machines created from <paramref name="box"/>, matched on name, provider and version.
    ///
    /// <para><b>Architecture deliberately not compared.</b> <c>Box#in_use?</c> compares it as well,
    /// and that comparison does not hold at the source this was checked against. A machine records
    /// its box's architecture as the Vagrantfile asked for it, <c>auto</c> by default, and the index
    /// stores it as JSON text; the remove action builds the box it compares against from
    /// <c>BoxCollection#all</c>, whose architecture is a Ruby symbol such as <c>:amd64</c>, or nil for
    /// an older layout. A string never equals a symbol in Ruby, so a machine on an architecture
    /// folder is never found in use. Ignoring the field can only find more machines, never fewer.</para>
    /// </summary>
    public IReadOnlyList<VagrantMachine> CreatedFrom(VagrantBoxVersion box) =>
        [.. Machines.Where(machine => machine.Box == box)];
}

/// <summary>
/// Reads <c>data\machine-index\index</c> in Vagrant's home (<c>lib/vagrant/machine_index.rb</c>,
/// hashicorp/vagrant at <c>dc55920a</c>): a JSON object whose <c>version</c> is 1 and whose
/// <c>machines</c> maps each machine's id to its record. A machine's box is that record's
/// <c>extra_data.box</c>, with <c>name</c>, <c>provider</c>, <c>architecture</c> and <c>version</c>,
/// written by <c>Machine#id=</c> and <c>Machine#recover_machine</c>.
/// </summary>
internal static class VagrantMachineIndex
{
    /// <summary>
    /// Far beyond any index Vagrant writes, at well under a kilobyte a machine, and small enough that a
    /// file grown past it is not that index.
    /// </summary>
    private const int MaximumBytes = 16 * 1024 * 1024;

    /// <summary>The index of the home at <paramref name="home"/>.</summary>
    public static string PathIn(string home) => Path.Combine(home, "data", "machine-index", "index");

    /// <summary>
    /// The machines the index records. No file is an index with no machines in it, which is how
    /// Vagrant reads it too; a file Windows would not describe, or one that is not an index Vagrant
    /// would load, is <see cref="VagrantMachineIndexReading.Unread"/>.
    /// </summary>
    public static VagrantMachineIndexReading Read(string home)
    {
        var path = PathIn(home);

        switch (LongPath.ProbeFile(path))
        {
            case PathPresence.Absent:
                return new VagrantMachineIndexReading([], null);

            case PathPresence.Refused:
                return new VagrantMachineIndexReading([], new UnreadFile(path, Unreached: true));
        }

        using var document = BoundedJsonFile.Read(path, MaximumBytes);

        return document is not null && Machines(document.RootElement) is { } machines
            ? new VagrantMachineIndexReading(machines, null)
            : new VagrantMachineIndexReading([], new UnreadFile(path, Unreached: false));
    }

    /// <summary>
    /// The machines in an index Vagrant would load, or null where it would refuse it: Vagrant raises on
    /// any <c>version</c> but 1, so a later format is one whose records this cannot vouch for.
    /// </summary>
    private static IReadOnlyList<VagrantMachine>? Machines(JsonElement index)
    {
        if (!index.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var number)
            || number != 1)
        {
            return null;
        }

        if (!index.TryGetProperty("machines", out var machines))
        {
            return [];
        }

        if (machines.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return
        [
            .. machines.EnumerateObject()
                .Select(entry => entry.Value)
                .Where(entry => entry.ValueKind == JsonValueKind.Object)
                .Select(Machine)
                .OfType<VagrantMachine>(),
        ];
    }

    private static VagrantMachine? Machine(JsonElement entry)
    {
        if (!entry.TryGetProperty("extra_data", out var extra)
            || extra.ValueKind != JsonValueKind.Object
            || !extra.TryGetProperty("box", out var box)
            || box.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return BoundedJsonFile.StringProperty(box, "name") is { } name
            && BoundedJsonFile.StringProperty(box, "provider") is { } provider
            && BoundedJsonFile.StringProperty(box, "version") is { } version
                ? new VagrantMachine(
                    BoundedJsonFile.StringProperty(entry, "name") ?? "(unnamed)",
                    BoundedJsonFile.StringProperty(entry, "vagrantfile_path"),
                    new VagrantBoxVersion(name, provider, version))
                : null;
    }
}
