using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The type declarations TypeScript's language service downloads for the JavaScript projects an editor
/// opens, one folder per TypeScript version (57.7 MB across four versions on the machine this was
/// measured on). A folder for a version no editor runs any more is never read again.
///
/// <para><b>Where it is.</b> <c>getGlobalTypingsCacheLocation</c> in TypeScript 6.0's
/// <c>src/tsserver/nodeServer.ts</c> joins <c>LOCALAPPDATA</c> with <c>Microsoft/TypeScript</c> and
/// <c>versionMajorMinor</c> on <c>win32</c>, and the native TypeScript 7 server joins the user's cache
/// folder with the same two segments (<c>osvfs.GetGlobalTypingsCacheLocation</c>). Neither reads a
/// setting that moves it: the installer's <c>--globalTypingsCacheLocation</c> is what the server hands
/// it, never what an editor passes. <c>versionMajorMinor</c> is checked against
/// <c>(\d+\.\d+)</c> by TypeScript's own release script, so every version folder is two numbers and a
/// dot, and anything else in the root is not recognised.</para>
///
/// <para><b>What is in a version folder.</b> The installer writes <c>package.json</c> and runs
/// <c>npm install</c> there, with the folder as its working directory, for the registry of type
/// packages and for each <c>@types</c> package a project needs. So the folder is an npm project of
/// downloaded declarations, and the next project opened with that version installs what it needs
/// again.</para>
///
/// <para><b>§5.1 was asked and the answer is no.</b> Neither TypeScript nor VS Code has a command
/// that clears it.</para>
///
/// <para><b>In use is found by where a program works, not by a name.</b> No process is called
/// <c>tsserver</c>: VS Code runs the server and its installer inside its own executable, other editors
/// run them under Node, and TypeScript 7 runs as <c>tsc</c>. What writes into a version folder is the
/// npm install, whose working directory is that folder, so a folder a program is working in, or holds
/// a file open in, is held back (<see cref="LiveTreeVeto"/>).</para>
/// </summary>
public sealed partial class TypeScriptTypingsCacheProvider : CleanupProviderBase
{
    /// <summary>One TypeScript version's typings: its major and minor version, as the release script requires.</summary>
    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex RecognisedChild();

    private const string UnrecognisedReason = "Not a TypeScript version's typings folder, so it is left alone.";

    private const string InUseReason = "A running program is using this version's typings, so it is left alone.";

    private readonly ILiveTreeInspector _liveTrees;

    public TypeScriptTypingsCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ILiveTreeInspector? liveTrees = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner)
        => _liveTrees = liveTrees ?? LiveTreeInspector.Default;

    public override string Id => "typescript-typings";

    public override string Name => "TypeScript typings cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    /// <summary>Each folder is one TypeScript version's typings.</summary>
    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "The next time an editor opens a JavaScript project with one of these TypeScript versions, it "
        + "downloads the type declarations that project needs again, so completions for its libraries "
        + "take a moment longer to appear once.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.PackageCaches,
        Application = "TypeScript's language service, which VS Code and other editors use for "
            + "JavaScript and TypeScript",
        Publisher = "Microsoft",
        Purpose = "When an editor opens a JavaScript project, TypeScript downloads type declarations for "
            + "the libraries it uses, so the editor can offer completions. It keeps them in one folder per "
            + "TypeScript version and never removes a folder, so every version an editor has run leaves "
            + "one behind.",
        Recommendation = "Every file here is a download TypeScript fetches again when a project needs it. "
            + "Your projects and their own packages are untouched.",
    };

    /// <summary>
    /// None. The server has no process of its own name, and warning on every editor or every Node
    /// process would fire on nearly every scan. A version folder in use is held back by where its
    /// program works instead.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => [];

    /// <summary>The cache, which nothing moves.</summary>
    public string Root => Path.Combine(Environment.LocalAppData, "Microsoft", "TypeScript");

    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        ToolRoot.Folders(
            Root,
            "This is TypeScript's typings cache. Deguffer removes whole version folders from it and "
            + "nothing else.",
            name => RecognisedChild().IsMatch(name)),
    ];

    /// <summary>The cache whole: a version folder is any child named by a version, which no narrower place can name.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>([CleanedPlace.Whole(Root)]);

    public override void InvalidateCaches()
    {
        _liveTrees.Invalidate();
        base.InvalidateCaches();
    }

    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(LongPath.DirectoryMayExist(Root));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var root = Root;

        if (NothingToPlanFor(root, $"TypeScript has not downloaded any typings for this user — no {root} directory.") is { } nothing)
        {
            return nothing;
        }

        // Built from a constant below the profile, so nothing has classified it: a link here would put
        // every removal on its far side while each survivor resolved through it and passed.
        if (DerivedPath.FirstObstacleBetween(Environment.LocalAppData, root) is { } obstacle)
        {
            return UnexaminedPlan(
                $"Leaving '{root}' alone: '{obstacle.Path}' is a link to somewhere else, or Windows would not "
                + "describe it, and Deguffer does not look through it.");
        }

        var notes = new List<PlanNote>();
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var spared = new List<(string Path, string Reason)>();

        var scan = ChildDirectories.Under(root);

        if (scan.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(root));
        }

        foreach (var link in scan.Links)
        {
            var path = LongPath.Display(link.FullName);

            notes.Add(CacheLevelWalk.Note(path));
            spared.Add((path, CacheLevelWalk.LinkReason));
        }

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            if (!RecognisedChild().IsMatch(child.Name))
            {
                var survivor = (LongPath.Display(child.FullName), UnrecognisedReason);

                notes.Add(CacheLevelWalk.SparedNote(survivor));
                spared.Add(survivor);
                continue;
            }

            versions.Add(LongPath.Display(child.FullName), child.Name);
        }

        // Each version folder is its own project: an install working in one is using none of the others.
        var live = LiveTreeVeto.Apply(
            _liveTrees,
            [.. versions.Keys.Select(folder => new RecognisedBuildDirectory(folder, folder))],
            lockFiles: [],
            ct);

        if (LiveTreeVeto.NoteFor(live.Vetoed, vetoed => $"TypeScript {versions[vetoed.Directory]}'s typings") is { } held)
        {
            notes.Add(held);
        }

        if (LiveTreeVeto.UncheckedNote(live.Unchecked.Count) is { } incomplete)
        {
            notes.Add(incomplete);
        }

        var targets = live.Cleared.Select(cleared =>
        {
            var version = versions[cleared.Path];

            return new DeletionTarget(
                cleared.Path,
                $"Type declarations TypeScript {version} downloaded, fetched again when a project needs them.",
                Identity: new ItemIdentity(version, $"TypeScript {version}"),
                UseCheck: cleared.StillUnused);
        }).ToList();

        if (targets.Count == 0 && spared.Count == 0 && live.Held.Count == 0 && !scan.Unreadable)
        {
            return EmptyPlan($"TypeScript's typings cache in {root} is empty.");
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect(
            [
                (root, "TypeScript's typings cache folder itself must survive — only version folders inside it are removed."),
                .. spared,
                .. live.Vetoed.Select(v => (v.Directory, InUseReason)),
                .. live.Unchecked.Select(path => (path, LiveTreeVeto.UncheckedReason)),
            ]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = scan.Unreadable,
            WasNotExamined = targets.Count == 0 && (spared.Count > 0 || live.Held.Count > 0),
        };
    }
}
