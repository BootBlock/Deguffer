using Deguffer.Core.Safety;

namespace Deguffer.Core.Startup;

/// <summary>
/// Reads every entry Windows starts at sign-in from the six sources §7.5 names, each once, with its
/// program, its publisher and whether it starts. It reads and decides nothing about what may change.
/// </summary>
public sealed class StartupReader(
    IStartupRegistry registry,
    StartupFolderReader folders,
    IShortcutReader shortcuts,
    IProgramFiles programs,
    IPackagedStartupTasks packaged)
{
    /// <summary>The reader the app runs with.</summary>
    public static StartupReader Default { get; } = new(
        WindowsStartupRegistry.Default,
        StartupFolderReader.Current,
        ShellShortcutReader.Default,
        WindowsProgramFiles.Default,
        WindowsPackagedStartupTasks.Default);

    private const string ShortcutExtension = ".lnk";

    public StartupReading Read(CancellationToken ct)
    {
        var entries = new List<StartupEntry>();
        var notes = new List<string>();

        foreach (var source in StartupSources.RunKeys)
        {
            ct.ThrowIfCancellationRequested();
            ReadRun(source, entries, notes);
        }

        foreach (var source in StartupSources.Folders)
        {
            ct.ThrowIfCancellationRequested();
            ReadFolder(source, entries, notes);
        }

        ReadPackaged(entries, notes, ct);

        entries.Sort(static (a, b) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title) is var byTitle and not 0
                ? byTitle
                : a.Key.Source.CompareTo(b.Key.Source));

        return new StartupReading(entries, notes);
    }

    private void ReadRun(StartupSource source, List<StartupEntry> entries, List<string> notes)
    {
        var run = registry.ReadRun(source);

        if (run.Presence is PathPresence.Refused)
        {
            notes.Add($"Windows would not open {source.RunLocation()}, so the entries it holds are not listed.");
            return;
        }

        var approvals = Approvals(source, notes);

        foreach (var (name, value) in run.Values)
        {
            // Windows runs the expansion of a REG_EXPAND_SZ.
            var command = value.Expanded ?? value.Text;

            var line = CommandLine.Read(command, programs.Probe);
            var program = line?.Presence is not null ? line.Executable : null;

            entries.Add(Entry(
                new StartupKey(source, name),
                name,
                command,
                program,
                ProgramStandings.From(line?.Presence),
                ApprovalOf(approvals, name),
                runValue: value));
        }
    }

    private void ReadFolder(StartupSource source, List<StartupEntry> entries, List<string> notes)
    {
        var listed = folders.List(source);

        if (listed.Presence is PathPresence.Refused)
        {
            notes.Add(listed.Folder is null
                ? $"Windows would not say where the {source.Describe().ToLowerInvariant()} is, so what it holds is not listed."
                : $"Windows would not list {listed.Folder}, so what it holds is not listed.");
            return;
        }

        if (listed.Items.Count == 0)
        {
            return;
        }

        var approvals = Approvals(source, notes);

        foreach (var item in listed.Items)
        {
            string? command;
            string? program;
            PathPresence? presence;

            if (item.Name.EndsWith(ShortcutExtension, StringComparison.OrdinalIgnoreCase))
            {
                var target = shortcuts.Read(item.Path);
                program = target?.Target is { } path && Path.IsPathFullyQualified(path) ? path : null;
                presence = program is null ? null : programs.Probe(program);
                command = target?.Target is { } named ? $"{named} {target.Arguments}".Trim() : null;
            }
            else
            {
                // Anything else in the folder is opened as it is, so it is its own program.
                program = item.Path;
                presence = PathPresence.Present;
                command = item.Path;
            }

            entries.Add(Entry(
                new StartupKey(source, item.Name),
                Path.GetFileNameWithoutExtension(item.Name),
                command,
                program,
                ProgramStandings.From(presence),
                ApprovalOf(approvals, item.Name),
                fileStamp: item.Stamp));
        }
    }

    private void ReadPackaged(List<StartupEntry> entries, List<string> notes, CancellationToken ct)
    {
        var read = packaged.Read(ct);

        if (!read.Complete)
        {
            notes.Add("Windows did not describe every app package installed for this account, so some packaged apps' startup tasks may be missing.");
        }

        foreach (var task in read.Tasks)
        {
            var standing = task.Program is { } program ? ProgramStandings.From(programs.Probe(program)) : ProgramStanding.Unknown;

            entries.Add(new StartupEntry(
                new StartupKey(StartupSource.PackagedTask, task.TaskId, task.PackageFamilyName),
                task.Name,
                Command: null,
                task.Program,
                standing,
                task.Publisher,
                StartupTaskManifest.StateOf(task.State),
                Approval: null));
        }
    }

    private StartupEntry Entry(
        StartupKey key,
        string name,
        string? command,
        string? program,
        ProgramStanding standing,
        Approval approval,
        StoredValue? runValue = null,
        FileStamp? fileStamp = null)
    {
        var described = program is not null && standing is ProgramStanding.Present
            ? programs.Describe(program)
            : ProgramDescription.None;

        return new StartupEntry(
            key,
            described.Description ?? name,
            command,
            program,
            standing,
            described.Company,
            approval.State,
            approval,
            runValue,
            fileStamp);
    }

    private KeyValues Approvals(StartupSource source, List<string> notes)
    {
        var approvals = registry.ReadApprovals(source);

        if (approvals.Presence is PathPresence.Refused)
        {
            notes.Add($"Windows would not open {source.ApprovalLocation()}, so whether its entries start is unknown.");
        }

        return approvals;
    }

    private static Approval ApprovalOf(KeyValues approvals, string name) =>
        approvals.Presence is PathPresence.Refused ? Approval.Unreadable : Approval.Of(approvals.Find(name));
}
