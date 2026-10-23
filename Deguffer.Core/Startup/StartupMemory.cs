using Deguffer.Core.Memory;

namespace Deguffer.Core.Startup;

/// <summary>What an entry's program holds now: how many processes run it, and their private working set.</summary>
/// <param name="PrivateWorkingSet">
/// The sum over those processes, a lower bound for §7.2's reasons, or null where none runs. Never zero
/// in place of a figure.
/// </param>
public sealed record RunningProgram(int Processes, long? PrivateWorkingSet)
{
    public static RunningProgram NotRunning { get; } = new(0, null);

    public bool IsRunning => Processes > 0;
}

/// <summary>What the programs Windows starts at sign-in hold now, from one reading of the process table.</summary>
/// <param name="Figures">
/// Whether §7.2's check let the table's per-process figures be used. Where it did not, no process can be
/// told apart from a successor holding its identifier, so nothing is matched and the page says why.
/// </param>
/// <param name="Complete">False where the process table was cut short, so a program may run unseen.</param>
public sealed record StartupMemoryReading(
    IReadOnlyDictionary<StartupKey, RunningProgram> Programs,
    ProcessFigures Figures,
    bool Complete)
{
    public static StartupMemoryReading None { get; } = new(new Dictionary<StartupKey, RunningProgram>(), ProcessFigures.NothingToCheckAgainst, Complete: false);

    /// <summary>What <paramref name="key"/>'s program holds, or null where nothing could be said about it.</summary>
    public RunningProgram? For(StartupKey key) => Programs.TryGetValue(key, out var running) ? running : null;
}

/// <summary>
/// Matches running processes to the programs startup entries name (§7.5): by full path, and only after
/// the image name in the process table has narrowed the processes to those that could match, because
/// the table holds the name alone and each full path is an open of the process.
/// </summary>
public static class StartupMemory
{
    public static StartupMemoryReading Match(StartupReading reading, ProcessMemoryTable table, IProcessImages images)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(images);

        var named = reading.Entries.Where(e => e.Program is not null).ToList();

        if (table.Figures is not ProcessFigures.Checked)
        {
            return new StartupMemoryReading(new Dictionary<StartupKey, RunningProgram>(), table.Figures, table.Complete);
        }

        var names = named.Select(e => Path.GetFileName(e.Program!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var counts = named.ToDictionary(e => e.Key, _ => (Processes: 0, Bytes: 0L));

        foreach (var process in table.Processes)
        {
            if (!names.Contains(process.Name) || process.CreationTime is not { } created
                || images.ImagePathOf(process.ProcessId, created) is not { } image)
            {
                continue;
            }

            foreach (var entry in named.Where(e => StartupReading.SamePath(e.Program!, image)))
            {
                var (processes, bytes) = counts[entry.Key];
                counts[entry.Key] = (processes + 1, bytes + (process.PrivateWorkingSet ?? 0));
            }
        }

        return new StartupMemoryReading(
            counts.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Processes == 0 ? RunningProgram.NotRunning : new RunningProgram(pair.Value.Processes, pair.Value.Bytes)),
            table.Figures,
            table.Complete);
    }
}
