using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The logs Claude Code writes for every MCP server it starts: one folder per server per project, and
/// one file for each time the server ran (320 KB across 242 files on the machine this was measured on,
/// the oldest ten weeks old).
///
/// <para><b>Tier 3, and separate from <see cref="ClaudeCodeDerivedStateProvider"/> for that reason
/// alone.</b> A plan carries one tier. §3's Tier 1 requires that whatever produced the content
/// re-creates it, and nothing re-creates the log of a server run that has ended — the argument
/// <see cref="VsCodeLogProvider"/>, <see cref="EpicLauncherLogProvider"/> and
/// <see cref="WindowsServicingLogProvider"/> each settled the same way. Somebody reporting a fault in an
/// MCP server has the only copy of what it said here.</para>
///
/// <para><b>Outside Claude Code's own folder, and outside its clean-up.</b> These live in the cache
/// folder of Claude Code's command-line tool under <c>%LOCALAPPDATA%</c>, which
/// <see cref="ClaudeCodeHome.ConfigDirectoryVariable"/> does not move. Eleven of the measured files were
/// older than the 30 days after which Claude Code removes a session's own folders, so nothing clears
/// these.</para>
///
/// <para><b>§5.2.</b> The cache folder holds one folder per project, named from the project's path.
/// Neither of those is ever a target, and inside a project's folder only a folder named for an MCP
/// server's log is recognised. Anything else in either is Tier 4 by construction.</para>
///
/// <para><b>No age filter, on <see cref="VsCodeLogProvider"/>'s reasoning.</b> A log written this
/// morning may be the one somebody needs. The tier keeps the row unselected, the confirmation says the
/// loss is permanent, and each folder shows when it was last written to.</para>
///
/// <para>§5.1 does not apply: Claude Code has no command that clears its server logs.</para>
/// </summary>
public sealed class ClaudeCodeMcpLogProvider : CleanupProviderBase
{
    /// <summary>What Claude Code names a server's log folder: this, then the server's name.</summary>
    public const string LogFolderPrefix = "mcp-logs-";

    private const string LogReason =
        "The log of one MCP server Claude Code ran for this project: a file for each time the server started. "
        + "Nothing re-creates the log of a run that has already ended.";

    private const string ToolFolderReason =
        "The folder Claude Code's command-line tool keeps its own files in. Only the MCP server logs inside it "
        + "are removed.";

    private const string CacheFolderReason =
        "Claude Code's per-project log folders. Only the MCP server logs inside them are removed.";

    private const string ProjectFolderReason =
        "The folder Claude Code keeps one project's logs in. Only the MCP server logs inside it are removed.";

    private Survey? _survey;
    private IReadOnlyList<ToolRoot>? _toolRoots;

    public ClaudeCodeMcpLogProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
    }

    public override string Id => "claude-code-mcp-logs";

    public override string Name => "Claude Code MCP server logs";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The record of what each MCP server reported while Claude Code ran it is destroyed, so none of it can be "
        + "attached to a bug report afterwards. Claude Code keeps writing new logs exactly as before, and your "
        + "servers and conversations are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Claude Code, Anthropic's coding agent, and the MCP servers it runs",
        Publisher = "Anthropic writes the logs; each MCP server is published by its own author",
        Purpose = "Every time Claude Code starts an MCP server it writes what that server reported to a new "
            + "file, in a folder per server per project. They sit outside Claude Code's own folder, where its "
            + "clean-up of old sessions does not reach, so they are never removed.",
        Recommendation = "A log is the record of what happened, and nothing re-creates it. Clear these once you "
            + "are not diagnosing a server. The row stays unticked and shows how recently each folder was "
            + "written to.",
    };

    /// <summary>§5.3. Claude Code appends to the log of every server it is running.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["claude"];

    /// <summary>
    /// Where Claude Code's command-line tool keeps its per-project logs. The tool names its folder
    /// <c>claude-cli</c>, and the library it uses for per-user folders appends <c>-nodejs</c> and puts
    /// its cache in <c>Cache</c> under <c>%LOCALAPPDATA%</c> on Windows.
    /// </summary>
    public string CacheFolder => Path.Combine(Environment.LocalAppData, "claude-cli-nodejs", "Cache");

    /// <summary>
    /// Presence is a log actually on disk, or a folder that refused to be listed. A project folder
    /// holding none is what every project Claude Code has opened without an MCP server leaves.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Look(ct) is { } survey
            && (survey.Targets.Count > 0 || survey.Declined.Count > 0 || survey.Unreadable));

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: the cache folder, recognising nothing, and each
    /// project's folder, recognising a server's log folder by its name.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        _toolRoots ??= Look() is { } survey
            ?
            [
                new ToolRoot(CacheFolder, CacheFolderReason, static _ => false),
                .. survey.ProjectFolders.Select(folder => new ToolRoot(folder, ProjectFolderReason, IsLogFolderName)),
            ]
            : [];

    public override void InvalidateCaches()
    {
        _survey = null;
        _toolRoots = null;
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var cache = CacheFolder;

        if (!LongPath.DirectoryExists(cache))
        {
            return EmptyPlan("Claude Code has written no MCP server logs for this user.");
        }

        if (LongPath.IsReparsePoint(cache))
        {
            return UnexaminedPlan(
                $"Leaving '{cache}' alone: it is a link to somewhere else, and Deguffer does not look through a link.");
        }

        var survey = Look(ct)!;
        var notes = new List<PlanNote>(survey.Notes);

        if (survey.Spared > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"{survey.Spared} other {(survey.Spared == 1 ? "item is" : "items are")} left alone beside the "
                + "server logs."));
        }

        if (survey.Targets.Count == 0 && survey.Declined.Count == 0 && !survey.Unreadable)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                "Claude Code is holding no MCP server logs on this machine."));
        }

        var (steps, measured) = await PlanDeletionsAsync(survey.Targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (survey.Targets.Count > 0 && BuildRunningProcessNote() is { } warning)
        {
            notes.Add(warning);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect(
                [.. survey.Survivors.Concat(survey.Declined).DistinctBy(s => s.Path, StringComparer.OrdinalIgnoreCase)]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = survey.Unreadable,
            WasNotExamined = survey.Targets.Count == 0 && survey.Declined.Count > 0,
        };
    }

    /// <summary>Whether a child of a project's folder is named like a server's log folder.</summary>
    private static bool IsLogFolderName(string name) =>
        name.Length > LogFolderPrefix.Length && name.StartsWith(LogFolderPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One look at the cache folder and each project's folder in it, memoised for the life of a planning
    /// pass (G4). Presence, planning and the declaration all read it.
    /// </summary>
    private Survey? Look(CancellationToken ct = default) => _survey ??= Examine(ct);

    private Survey? Examine(CancellationToken ct)
    {
        var cache = CacheFolder;

        if (!LongPath.DirectoryExists(cache) || LongPath.IsReparsePoint(cache))
        {
            return null;
        }

        var projects = new List<string>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();
        var notes = new List<PlanNote>();

        var survivors = new List<(string Path, string Reason)>
        {
            (Path.GetDirectoryName(cache)!, ToolFolderReason),
            (cache, CacheFolderReason),
        };

        var spared = 0;

        if (FolderEntries.Of(cache) is not { } folders)
        {
            notes.Add(UnreadableRoot.Note(cache));
            return new Survey(projects, targets, survivors, declined, notes, spared, Unreadable: true);
        }

        var unreadable = false;

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();

            var path = LongPath.Display(folder.FullName);

            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Decline(path);
                continue;
            }

            if (folder is not DirectoryInfo)
            {
                survivors.Add((path, "Not something Deguffer recognises beside Claude Code's log folders, so it is left alone."));
                spared++;
                continue;
            }

            projects.Add(path);
            survivors.Add((path, ProjectFolderReason));

            if (FolderEntries.Of(path) is not { } entries)
            {
                notes.Add(UnreadableRoot.Note(path));
                unreadable = true;
                continue;
            }

            foreach (var entry in entries)
            {
                var entryPath = LongPath.Display(entry.FullName);

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Decline(entryPath);
                }
                else if (entry is DirectoryInfo && IsLogFolderName(entry.Name))
                {
                    // §7's age, from the newest write one level down: a server's log folder gains a file
                    // each run, which moves the folder, and a run's file is appended to, which does not.
                    targets.Add(new DeletionTarget(entryPath, LogReason, DirectoryAge.Of(entryPath, ct)));
                }
                else
                {
                    survivors.Add((entryPath, "Not an MCP server's log, so it is left alone."));
                    spared++;
                }
            }
        }

        return new Survey(projects, targets, survivors, declined, notes, spared, unreadable);

        void Decline(string path)
        {
            notes.Add(CacheLevelWalk.Note(path));
            declined.Add((path, CacheLevelWalk.LinkReason));
        }
    }

    /// <param name="ProjectFolders">Each project's folder, for the declaration.</param>
    /// <param name="Targets">The server log folders.</param>
    /// <param name="Survivors">Every folder and item left alone.</param>
    /// <param name="Declined">Links, named and never followed.</param>
    /// <param name="Notes">What the look had to say.</param>
    /// <param name="Spared">How many unrecognised items were left alone, for one sentence rather than one each.</param>
    /// <param name="Unreadable">Whether a folder refused to be listed.</param>
    private sealed record Survey(
        IReadOnlyList<string> ProjectFolders,
        IReadOnlyList<DeletionTarget> Targets,
        IReadOnlyList<(string Path, string Reason)> Survivors,
        IReadOnlyList<(string Path, string Reason)> Declined,
        IReadOnlyList<PlanNote> Notes,
        int Spared,
        bool Unreadable);
}
