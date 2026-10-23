using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Scanning;
using Deguffer.Core.Startup;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One entry on the Startup page. Written over in place when a new reading arrives, so the list keeps
/// its selection and the reader keeps their place (G4).
/// </summary>
public sealed partial class StartupRow : ObservableObject
{
    public StartupRow(StartupEntry entry, StartupMarks marks) => Show(entry, marks);

    public StartupKey Key => Entry.Key;

    [ObservableProperty]
    public partial StartupEntry Entry { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; }

    /// <summary>Who made it, where it comes from, and the entry's own name where the title is the program's description.</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; }

    [ObservableProperty]
    public partial string State { get; private set; }

    [ObservableProperty]
    public partial StartupState StateKind { get; private set; }

    [ObservableProperty]
    public partial string StateReason { get; private set; }

    /// <summary>What the entry runs, or where it is, for the badge's flyout.</summary>
    [ObservableProperty]
    public partial string Runs { get; private set; }

    [ObservableProperty]
    public partial string Missing { get; private set; }

    [ObservableProperty]
    public partial bool IsMissing { get; private set; }

    [ObservableProperty]
    public partial string Shield { get; private set; }

    [ObservableProperty]
    public partial bool HasShield { get; private set; }

    /// <summary>What its program holds now, or empty where nothing is known to be running.</summary>
    [ObservableProperty]
    public partial string Memory { get; private set; }

    [ObservableProperty]
    public partial bool HasMemory { get; private set; }

    /// <summary>Whether its program is known not to be running, which the row says in words rather than as a figure.</summary>
    [ObservableProperty]
    public partial bool IsNotRunning { get; private set; }

    [ObservableProperty]
    public partial string MemoryNote { get; private set; }

    /// <summary>Everything the row's marks say, for a screen reader.</summary>
    [ObservableProperty]
    public partial string Description { get; private set; }

    public void Show(StartupEntry entry, StartupMarks marks)
    {
        Entry = entry;
        Title = entry.Title;
        Detail = string.Join(" · ", new[]
        {
            string.Equals(entry.Title, entry.Key.Name, StringComparison.OrdinalIgnoreCase) ? null : entry.Key.Name,
            entry.Publisher,
            entry.Key.Source.Describe(),
        }.Where(p => p is not null));
        State = marks.State;
        StateKind = entry.State;
        StateReason = marks.StateReason;
        Runs = entry.Command is { } command ? $"Runs: {command}"
            : entry.Program is { } program ? $"Starts: {program}"
            : "The program it starts is not named in a way Deguffer can read.";
        Missing = marks.Missing ?? string.Empty;
        IsMissing = marks.Missing is not null;
        Shield = marks.Shield ?? string.Empty;
        HasShield = marks.Shield is not null;
        Memory = marks.Memory is { } bytes ? FreeSpace.Format(bytes) : string.Empty;
        HasMemory = marks.Memory is not null;
        IsNotRunning = marks.IsRunning is false;
        MemoryNote = marks.MemoryNote;
        Description = string.Join(" ", new[] { $"{marks.State}.", marks.StateReason, marks.Missing, marks.Shield, HasMemory ? $"{Memory}." : null, marks.MemoryNote }
            .Where(p => p is not null));
    }
}
