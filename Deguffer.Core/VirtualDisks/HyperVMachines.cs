using System.Text;
using System.Text.Json;
using Deguffer.Core.Safety;

namespace Deguffer.Core.VirtualDisks;

/// <summary>
/// Every Hyper-V virtual machine's disks, as Hyper-V itself lists them, through one read-only query of
/// the Hyper-V module in Windows PowerShell.
///
/// <para><b>Hyper-V's own answer, never its default folder.</b> Microsoft says the host's
/// <c>VirtualHardDiskPath</c> "is not a functional setting… the Hyper-V API… and <c>New-VHD</c> don't use
/// it" (the <c>Set-VMHost</c> page), so a disk can be anywhere, and an unattached <c>.vhdx</c> in that
/// folder can be WSL's, Docker's or another host's. Each machine is asked for its attached disks
/// (<c>Get-VMHardDiskDrive</c>), each checkpoint for the disks it keeps (the same cmdlet's
/// <c>-VMSnapshot</c>), and each disk for its parent (<c>Get-VHD</c>'s <c>ParentPath</c>), so the whole
/// chain of a machine with checkpoints is named and registered.</para>
///
/// <para><b>Hyper-V's answer is only an answer to an account it trusts.</b> Every Hyper-V cmdlet refused an
/// account that is neither elevated nor in Hyper-V Administrators where the survey measured it, and a
/// third-party write-up (Altaro, <c>altaro.com/hyper-v/?p=21601</c>) reports <c>Get-VM</c> returning no
/// machines at all, rather than failing, for such an account. So this process's own token is asked first
/// whether it is either (<see cref="IUserEnvironment.IsHyperVAdministrator"/>), and an account that is not
/// is told that whether it has machines is not known, never that it has none, with nothing started.</para>
/// </summary>
public sealed class HyperVMachines(ISystemDirectories system, IUserEnvironment environment, IProcessRunner runner)
{
    /// <summary>
    /// The query, in Windows PowerShell 5.1. It writes one JSON object: an <c>error</c> where Hyper-V
    /// failed, or each machine. It is run only for an account Hyper-V answers, and Windows PowerShell runs
    /// with this process's token, so it does not ask again. Every array is built with <c>@()</c> and the object
    /// is passed rather than piped, because Windows PowerShell's <c>ConvertTo-Json</c> otherwise writes a
    /// list of one as that one, and nothing at all for an empty list piped to it. A parent that will not
    /// be read is listed under <c>unfollowed</c>, so a chain cut short is said rather than hidden.
    /// </summary>
    internal const string Query =
        """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
        try {
            $machines = @(foreach ($vm in @(Get-VM)) {
                $attached = @(Get-VMHardDiskDrive -VM $vm | Where-Object { $_.Path } | ForEach-Object { [string]$_.Path })
                $checkpoints = @(Get-VMSnapshot -VM $vm)
                $kept = @(foreach ($checkpoint in $checkpoints) {
                    Get-VMHardDiskDrive -VMSnapshot $checkpoint | Where-Object { $_.Path } | ForEach-Object { [string]$_.Path } })
                $parents = @()
                $unfollowed = @()
                foreach ($disk in @($attached + $kept)) {
                    $next = $disk
                    while ($next) {
                        try { $next = [string](Get-VHD -Path $next).ParentPath } catch { $unfollowed += $next; $next = $null }
                        if ($next) { $parents += $next }
                    }
                }
                [pscustomobject]@{
                    name = [string]$vm.Name; state = [string]$vm.State; checkpoints = $checkpoints.Count
                    attached = $attached; kept = @($kept + $parents); unfollowed = $unfollowed }
            })
            ConvertTo-Json -InputObject ([pscustomobject]@{ machines = $machines }) -Depth 4 -Compress
        } catch {
            ConvertTo-Json -InputObject ([pscustomobject]@{ error = [string]$_.Exception.Message }) -Compress
        }
        """;

    /// <summary>The query as Windows PowerShell takes it whole, with no quoting for a command line to undo.</summary>
    internal static readonly string Arguments =
        "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(Query));

    /// <summary>
    /// The query's run, read as UTF-8. Windows PowerShell writes to a pipe in the console's OEM code page
    /// unless told otherwise, and its <c>ConvertTo-Json</c> leaves non-ASCII characters as they are, so a
    /// disk under <c>C:\Users\José</c> read in that page would be registered at a path that is not there,
    /// and protected as absent. The query sets its output to UTF-8, and this reads it so.
    /// </summary>
    internal static readonly ToolRun Run = ToolRun.Query.WritingIn(ToolOutputEncoding.Utf8);

    /// <summary>The answer is a few kilobytes for a host with many machines. Anything far larger is not it.</summary>
    private const int MaximumAnswerCharacters = 4 * 1024 * 1024;

    private const string Unanswered =
        "so whether this account has Hyper-V virtual machines, and how much of the drive their disks take, is not known.";

    /// <summary>Windows PowerShell, which ships with Windows and runs the Hyper-V module.</summary>
    public string PowerShell { get; } = NativeSystemTool.In(system, Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe"));

    /// <summary>
    /// The Hyper-V module's folder. It is there only where Hyper-V's management tools are installed, and
    /// holds a folder per module version, so the folder is asked about rather than one manifest.
    /// </summary>
    public string Module { get; } = NativeSystemTool.In(system, Path.Combine("WindowsPowerShell", "v1.0", "Modules", "Hyper-V"));

    /// <summary>
    /// Each machine's disks, or why Hyper-V would not say. Nothing is run where the module is not
    /// installed, or for an account Hyper-V would refuse, and a module folder Windows will not describe is
    /// asked anyway, because a refusal is not an absence. The module comes first, so a machine without
    /// Hyper-V's tools has no row for any account.
    /// </summary>
    public async Task<VirtualDiskInventory> ReadAsync(CancellationToken ct)
    {
        if (LongPath.ProbeDirectory(Module) == PathPresence.Absent)
        {
            return VirtualDiskInventory.Empty;
        }

        if (!environment.IsHyperVAdministrator)
        {
            return Problem(
                "Hyper-V answers only an elevated PowerShell or a member of the Hyper-V Administrators group, and "
                + $"Deguffer is running as neither, {Unanswered}");
        }

        var outcome = await runner.RunAsync(PowerShell, Arguments, Run, ct).ConfigureAwait(false);

        // The last line, because Windows PowerShell writes a warning a module gives while loading to standard
        // output, ahead of the one line of JSON.
        return outcome.LastLine is { } answer
            ? Parse(answer)
            : Problem($"Windows PowerShell could not ask Hyper-V for its virtual machines ({outcome.Message}), {Unanswered}");
    }

    /// <summary>The disks <paramref name="answer"/> names, which is the query's output.</summary>
    internal static VirtualDiskInventory Parse(string answer)
    {
        if (answer.Length > MaximumAnswerCharacters)
        {
            return Problem($"Hyper-V's answer was far larger than any list of machines, {Unanswered}");
        }

        try
        {
            using var document = JsonDocument.Parse(answer);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Problem($"Hyper-V's answer could not be read, {Unanswered}");
            }

            if (Text(root, "error") is { } error)
            {
                return Problem($"Hyper-V would not list its virtual machines ({error}), {Unanswered}");
            }

            return Property(root, "machines") is { ValueKind: JsonValueKind.Array } machines
                ? Read(machines)
                : Problem($"Hyper-V's answer could not be read, {Unanswered}");
        }
        catch (JsonException)
        {
            return Problem($"Hyper-V's answer could not be read, {Unanswered}");
        }
    }

    private static VirtualDiskInventory Read(JsonElement machines)
    {
        List<VirtualDisk> disks = [];
        List<string> problems = [];

        foreach (var entry in machines.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Text(entry, "name") is not { } name)
            {
                continue;
            }

            var machine = new VirtualMachineRecord(
                name,
                Property(entry, "checkpoints") is { ValueKind: JsonValueKind.Number } count && count.TryGetInt32(out var n) ? n : null,
                Text(entry, "state") == "Saved");

            Add(disks, Paths(entry, "attached"), new HyperVDisk(machine, MachineDiskRole.Current));
            Add(disks, Paths(entry, "kept"), new HyperVDisk(machine, MachineDiskRole.Checkpoint));

            if (Paths(entry, "unfollowed").FirstOrDefault() is { } unfollowed)
            {
                problems.Add(
                    $"Hyper-V would not describe {unfollowed}, a disk of the virtual machine '{name}', so the disks it is "
                    + "built on are not listed here.");
            }
        }

        return new VirtualDiskInventory(disks, problems);
    }

    /// <summary>
    /// Each path not already listed, so a disk attached now and kept by a checkpoint too is named once,
    /// as the disk the machine runs from.
    /// </summary>
    private static void Add(List<VirtualDisk> disks, IEnumerable<string> paths, HyperVDisk owner)
    {
        foreach (var path in paths)
        {
            if (!disks.Any(known => known.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                disks.Add(new VirtualDisk(path, owner, null));
            }
        }
    }

    /// <summary>Every full path in the named array. Hyper-V writes full paths, and anything else is not one.</summary>
    private static IEnumerable<string> Paths(JsonElement entry, string name) =>
        Property(entry, name) is { ValueKind: JsonValueKind.Array } paths
            ? paths.EnumerateArray()
                .Where(path => path.ValueKind == JsonValueKind.String)
                .Select(path => LongPath.Configured(path.GetString()))
                .OfType<string>()
            : [];

    private static string? Text(JsonElement entry, string name) =>
        Property(entry, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static JsonElement? Property(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) ? value : null;

    private static VirtualDiskInventory Problem(string problem) => new([], [problem]);
}
