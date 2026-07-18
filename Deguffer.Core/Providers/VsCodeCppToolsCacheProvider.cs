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
/// Only <c>ipch</c> is recognised: a hex name carries no meaning that can be checked, and §5.2's
/// dangerous direction is treating an unknown thing as safe.
/// </summary>
public sealed class VsCodeCppToolsCacheProvider : CleanupProviderBase
{
    /// <summary>
    /// The only child of the cpptools directory this provider recognises. Anything else — every
    /// hex-named workspace database directory included — is Tier 4 by construction.
    /// </summary>
    public static readonly DisposableChildSet DisposableChildren = new(
    [
        new ChildClassification(
            "ipch",
            SafetyTier.RegenerableCache,
            "Precompiled headers used only to speed up IntelliSense. The extension rebuilds them."),
    ]);

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
        var targets = new List<(string Path, string Reason)>();

        foreach (var child in EnumerateChildren())
        {
            ct.ThrowIfCancellationRequested();

            var classification = DisposableChildren.Classify(child.Name);

            if (!classification.Tier.IsOfferable())
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{child.Name}' alone: {classification.Reason}"));
                continue;
            }

            targets.Add((LongPath.Display(child.FullName), classification.Reason));
        }

        var measured = await MeasureAllAsync([.. targets.Select(t => t.Path)], ct).ConfigureAwait(false);

        var steps = new List<CleanupStep>(targets.Count);
        for (var i = 0; i < targets.Count; i++)
        {
            steps.Add(new DeleteDirectoryStep(targets[i].Path, targets[i].Reason)
            {
                Estimated = measured.Sizes[i],
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
            ProtectedPaths = BuildProtectedPaths(),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// §5.6. The root has to survive so the extension keeps writing where it expects to, and the
    /// workspace databases beside <c>ipch</c> are what an over-broad rule would take with it.
    /// </summary>
    private IReadOnlyList<ProtectedPath> BuildProtectedPaths() => Protect(
        (_root, "The cpptools root itself must survive — only its known-disposable children are removed."));

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
