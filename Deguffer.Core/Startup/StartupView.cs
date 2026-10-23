using Deguffer.Core.Memory;

namespace Deguffer.Core.Startup;

/// <summary>
/// One reading of the Startup page (§7.5): the entries, and what their programs hold now from one
/// reading of the process table taken with them.
/// </summary>
public sealed record StartupView(StartupReading Reading, StartupMemoryReading Memory)
{
    public static StartupView Empty { get; } = new(StartupReading.Empty, StartupMemoryReading.None);

    /// <summary>Read the entries, then the process table, then the full paths of the processes that could be their programs.</summary>
    public static StartupView Read(StartupReader reader, IMemorySource memory, IProcessImages images, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(images);

        var reading = reader.Read(ct);
        var table = memory.Read(ct).Processes;

        return new StartupView(reading, StartupMemory.Match(reading, table, images));
    }

    /// <summary>The line under the page's title: how many entries there are and how many start, and what was not read.</summary>
    public string Headline
    {
        get
        {
            var entries = Reading.Entries;
            var starting = entries.Count(e => e.State is StartupState.Starts);
            var disabled = entries.Count(e => e.State is StartupState.Disabled);
            var unknown = entries.Count - starting - disabled;

            var counts = entries.Count switch
            {
                0 => "Nothing is set to start at sign-in.",
                _ => $"{Plural(entries.Count, "entry", "entries")}: {starting} {(starting == 1 ? "starts" : "start")} at sign-in and {disabled} {(disabled == 1 ? "is" : "are")} disabled"
                    + (unknown > 0 ? $", and {unknown} {(unknown == 1 ? "is" : "are")} unknown." : "."),
            };

            var notes = Memory.Complete ? Reading.Notes : [.. Reading.Notes, "The process table was cut short, so a program may be running that this page does not show."];

            return string.Join(" ", [counts, .. notes]);
        }
    }

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
}
