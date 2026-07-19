using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The VS Code C/C++ extension's IntelliSense caches (~6 GB on the audited machine).
///
/// §5.1 does not apply in practice: the extension's own eviction is the
/// <c>C/C++: Reset IntelliSense Database</c> command in the editor's command palette, which has no
/// CLI form and in any case only clears the database for the workspace that is currently open. So
/// this is the path-based case, like Gradle.
///
/// The directory holds <c>ipch</c> alongside one hex-named directory per workspace ever opened.
/// <c>ipch</c> is recognised by name. The workspace directories cannot be — a hex name carries no
/// meaning that can be checked — so they are recognised by their contents instead, which is a
/// stronger test rather than a wider one. Anything matching neither is Tier 4 by construction,
/// because §5.2's dangerous direction is treating an unknown thing as safe.
/// </summary>
public sealed partial class VsCodeCppToolsCacheProvider : CleanupProviderBase
{
    /// <summary>
    /// The only child of the cpptools directory recognised <em>by name</em>. Anything else is Tier 4
    /// unless it can be recognised by its contents instead — see <see cref="WorkspaceDatabase"/>.
    /// </summary>
    public static readonly DisposableChildSet DisposableChildren = new(
    [
        new ChildClassification(
            "ipch",
            SafetyTier.RegenerableCache,
            "Precompiled headers used only to speed up IntelliSense. The extension rebuilds them."),
    ]);

    /// <summary>
    /// The browse database and its SQLite sidecars, plus the numbered variant that appears beside
    /// them (<c>.BROWSE.VC.2.DB</c>). Only the suffix is matched: the leading part of the filename
    /// derives from the workspace and is user-overridable via
    /// <c>C_Cpp.default.browse.databaseFilename</c>, so it may be anything at all — including
    /// empty, which is why the name can begin with the dot.
    ///
    /// Anchored with <c>\z</c> rather than <c>$</c>: <c>$</c> also matches before a trailing
    /// newline, and a check that decides whether a directory may be deleted should admit no such
    /// reading.
    /// </summary>
    [GeneratedRegex(@"\.BROWSE\.VC(\.\d+)?\.DB(-shm|-wal)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BrowseDatabaseFile();

    /// <summary>
    /// Recognises one workspace's IntelliSense database directory. Its name is a hash and proves
    /// nothing, so what identifies it is that it holds the browse database and nothing else — a
    /// stronger check than the name match it stands in for, never a wider one.
    ///
    /// The contents are regenerable: the extension rebuilds the database the next time that
    /// workspace is opened, and Microsoft's own guidance is that everything it writes here may be
    /// deleted safely. A directory holding anything unexpected fails the signature and stays Tier 4.
    /// </summary>
    public static readonly ContentSignature WorkspaceDatabase = new(BrowseDatabaseFile());

    private readonly string _root;

    public VsCodeCppToolsCacheProvider(
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
        _root = Path.Combine(Environment.LocalAppData, "Microsoft", "vscode-cpptools");
    }

    public override string Id => "vscode-cpptools";

    public override string Name => "VS Code C/C++ IntelliSense cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The next time a C++ project is opened, the extension rebuilds its precompiled headers. " +
        "IntelliSense is slower until that finishes.";

    /// <summary>
    /// §5.3. The workspace databases are SQLite with write-ahead logs, and the extension keeps them
    /// open; Microsoft's own guidance is to clear this after closing the editor.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames =>
        ["Code", "cpptools", "cpptools-srv"];

    /// <summary>The cpptools root. Exposed so tests can assert it is never targeted.</summary>
    public string RootPath => _root;

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(LongPath.DirectoryExists(_root));

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        if (!LongPath.DirectoryExists(_root))
        {
            return EmptyPlan("The VS Code C/C++ extension has no cache directory for this user.");
        }

        var notes = new List<PlanNote>();
        var targets = new List<(string Path, string Reason, DateTime? LastWritten)>();
        var declined = new List<(string Path, string Reason)>();

        foreach (var child in EnumerateChildren())
        {
            ct.ThrowIfCancellationRequested();

            var (classification, isPerWorkspace) = Classify(child, ct);

            if (!classification.Tier.IsOfferable())
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{child.Name}' alone: {classification.Reason}"));
                declined.Add((LongPath.Display(child.FullName), classification.Reason));
                continue;
            }

            targets.Add((
                LongPath.Display(child.FullName),
                classification.Reason,
                isPerWorkspace ? NewestWrite(child, ct) : null));
        }

        var measured = await MeasureAllAsync([.. targets.Select(t => t.Path)], ct).ConfigureAwait(false);

        var steps = new List<CleanupStep>(targets.Count);
        for (var i = 0; i < targets.Count; i++)
        {
            steps.Add(new DeleteDirectoryStep(targets[i].Path, targets[i].Reason)
            {
                Estimated = measured.Sizes[i],
                LastWritten = targets[i].LastWritten,
            });
        }

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (BuildRunningProcessNote() is { } warning)
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
            ProtectedPaths = BuildProtectedPaths(declined),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// §5.6. The root has to survive so the extension keeps writing where it expects to.
    ///
    /// Every child that was classified Tier 4 is protected by name as well, because since the
    /// workspace databases became reachable the deleted and the declined are siblings of the same
    /// shape, distinguished only by their contents. That is precisely when an over-broad rule takes
    /// one with the other, so the negative is verified against the specific directories this plan
    /// decided to spare rather than against the root alone.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths(
        IReadOnlyList<(string Path, string Reason)> declined) => Protect(
    [
        (_root, "The cpptools root itself must survive — only its known-disposable children are removed."),
        .. declined,
    ]);

    /// <summary>
    /// §5.2 by name first, then by content. The content signature only ever runs on a child the
    /// name check already rejected, and a child that fails it keeps that Tier 4 — so this widens
    /// what can be verified, never what is assumed.
    /// </summary>
    private static ChildDecision Classify(DirectoryInfo child, CancellationToken ct)
    {
        var byName = DisposableChildren.Classify(child.Name);

        if (byName.Tier.IsOfferable() || !WorkspaceDatabase.Matches(child.FullName, ct))
        {
            return new ChildDecision(byName, IsPerWorkspace: false);
        }

        return new ChildDecision(
            new ChildClassification(
                child.Name,
                SafetyTier.RegenerableCache,
                "One workspace's IntelliSense database, holding nothing but the browse database. " +
                "The extension rebuilds it the next time that workspace is opened."),
            IsPerWorkspace: true);
    }

    /// <summary>
    /// How a child was recognised, which decides whether it gets an age.
    /// </summary>
    /// <param name="IsPerWorkspace">
    /// True only for a child the content signature vouched for. §7 scopes the age column to
    /// per-workspace and per-project data, and those are the only children that are one workspace
    /// each. <c>ipch</c> is a whole-cache directory covering every workspace at once, so a single
    /// timestamp on it would be a number with nothing to mean — and a number the user might act on.
    /// </param>
    private readonly record struct ChildDecision(ChildClassification Classification, bool IsPerWorkspace);

    /// <summary>
    /// When this child was last written, for §7's age column, or null if it cannot be read.
    ///
    /// The newest file inside it, not the directory's own timestamp: SQLite rewrites the database
    /// in place, and a directory's mtime moves only when an entry is added, removed or renamed. A
    /// workspace opened daily for a year would otherwise report the age of its first build.
    ///
    /// Reading the children is sound precisely because these directories are flat — the content
    /// signature refuses any child holding a subdirectory — so there is no deeper level whose
    /// writes this would miss. The enumeration is the same one the signature just made, over at
    /// most a handful of entries.
    /// </summary>
    private static DateTime? NewestWrite(DirectoryInfo child, CancellationToken ct)
    {
        try
        {
            DateTime? newest = null;

            foreach (var file in child.EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();

                if (newest is null || file.LastWriteTimeUtc > newest)
                {
                    newest = file.LastWriteTimeUtc;
                }
            }

            return newest;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            // No timestamp is a real answer, and §7 renders it as unknown rather than as an age.
            return null;
        }
    }

    private IEnumerable<DirectoryInfo> EnumerateChildren()
    {
        try
        {
            return new DirectoryInfo(LongPath.Extended(_root))
                .EnumerateDirectories()
                .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return [];
        }
    }
}
