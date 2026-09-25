using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Zig's global build cache, which every Zig build on the machine shares and nothing ever trims.
/// ziglang/zig#15358 reports one cache holding about 45 copies of a single dependency, one per version
/// and build configuration. Researched from Zig's source at 0.11 to 0.16 rather than measured, because
/// no Zig toolchain was installed on the machine this was written against.
///
/// <para><b>§5.1 was asked and the answer is no.</b> No released Zig has a command that evicts the
/// global cache. The collector planned in ziglang/zig#30193 does not exist yet, so this is the
/// path-based case.</para>
///
/// <para><b>What goes is decided by what Zig re-creates.</b> <c>h</c>, <c>o</c>, <c>z</c>, <c>b</c> and
/// <c>tmp</c> are derived from source still on the disk, and Zig writes each again on demand. <c>p</c>
/// is not: it holds fetched packages, and for some of them it is the only copy on this machine — a
/// package fetched from a web address comes back only while that address still serves it, and anyzig
/// runs whole Zig compilers from it. So <c>p</c> is declared and left alone, on Cargo's reasoning about
/// <c>git\db</c>.</para>
///
/// <para><b><c>o</c> never goes without <c>h</c>.</b> A manifest in <c>h</c> names an output in
/// <c>o</c> by its path and trusts that it is there, and ziglang/zig#15358 records a build failing on a
/// missing <c>crt1.o</c> after <c>o</c> alone was deleted, until the whole cache was. So the two are one
/// step, the index first (<see cref="DeleteDirectoryStep.IndexedBy"/>), and Explore may remove
/// <c>h</c> alone but never <c>o</c>.</para>
///
/// <para>A project's own <c>.zig-cache</c> and <c>zig-pkg</c> sit inside the user's source tree, and
/// are not this provider's business.</para>
/// </summary>
public sealed class ZigCacheProvider : CleanupProviderBase
{
    /// <summary>Set by the user to move the global cache.</summary>
    public const string CacheVariable = "ZIG_GLOBAL_CACHE_DIR";

    /// <summary>The build outputs, which go only with their index.</summary>
    public const string Outputs = "o";

    /// <summary>The manifests naming each output in <see cref="Outputs"/>.</summary>
    public const string Index = "h";

    /// <summary>
    /// What may be deleted. Anything not named here is Tier 4 by construction, which is the direction
    /// §5.2 requires the unknown case to fail in.
    /// </summary>
    public static readonly DisposableChildSet Children = new(
    [
        new ChildClassification(
            Outputs,
            SafetyTier.RegenerableCache,
            "What Zig built: the language runtime and C libraries for each target, and programs built outside "
            + "a project. Removed together with the records in 'h' that point at them, so no build is left "
            + "expecting a file that is gone."),
        new ChildClassification(
            Index,
            SafetyTier.RegenerableCache,
            "Zig's records of which inputs produced each build output. Zig writes them again on the next build."),
        new ChildClassification(
            "z",
            SafetyTier.RegenerableCache,
            "Source files Zig has already parsed, kept in its own intermediate form. Zig parses them again."),
        new ChildClassification(
            "b",
            SafetyTier.RegenerableCache,
            "Generated files describing each build's target. Zig generates them again."),
        new ChildClassification(
            "tmp",
            SafetyTier.RegenerableCache,
            "Scratch space Zig builds and downloads in before it moves the result into place."),
        new ChildClassification(
            "p",
            SafetyTier.DoNotTouch,
            "Packages Zig fetched for projects. For some it is the only copy on this machine: one fetched "
            + "from a web address comes back only while that address still serves it, and tools such as "
            + "anyzig run Zig compilers from here."),
    ]);

    /// <summary>
    /// What Explore may remove on its own: every disposable child but <see cref="Outputs"/>, which goes
    /// only in the plan's step with its index.
    /// </summary>
    private static readonly HashSet<string> RemovableAlone = new(
        Children.DisposableNames.Where(name => !name.Equals(Outputs, StringComparison.OrdinalIgnoreCase)),
        StringComparer.OrdinalIgnoreCase);

    private const string LinkReason =
        "A link rather than a directory, so what it points at was never classified.";

    private readonly ISystemDirectories _system;

    public ZigCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _system = system ?? SystemDirectories.Current;
    }

    public override string Id => "zig";

    public override string Name => "Zig build cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The next Zig build compiles the language runtime and any C libraries it links again, and "
        + "re-parses the source it uses, so it takes longer once. Fetched packages, your projects and "
        + "the Zig compiler itself are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Zig, a programming language and C/C++ toolchain",
        Publisher = "the Zig Software Foundation",
        Purpose = "Every Zig build on the machine shares one cache of what it compiled: the language "
            + "runtime, C libraries built for each target, parsed source and the records tying them "
            + "together. Zig never removes anything from it, so it grows with every version and build "
            + "configuration used.",
        Recommendation = "Everything Deguffer removes here is compiled from source still on your disk, "
            + "and Zig compiles it again when a build needs it. Fetched packages stay, because for some "
            + "of them this is the only copy on the machine.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["zig", "zls"];

    /// <summary>
    /// What holds the outputs back at the clean. Only Zig itself writes a manifest, and one written while
    /// the index is being removed would name an output about to go. The language server is left out: it
    /// runs for as long as an editor is open, and it builds through Zig, which this already names.
    /// </summary>
    private static readonly IReadOnlyList<string> BuildingProcessNames = ["zig"];

    /// <summary>Where Zig keeps its cache when <see cref="CacheVariable"/> is unset.</summary>
    public string DefaultRoot => Path.Combine(Environment.LocalAppData, "zig");

    /// <summary>
    /// The global cache, honouring <see cref="CacheVariable"/>, or null where that names nothing this
    /// can examine as Zig's. Zig resolves a relative value against the working directory of the build,
    /// which Deguffer is not, so there is no correct reading of one. And a folder that holds a temporary
    /// folder or one Windows is built out of is not examined as Zig's, whatever the variable says: the
    /// names Zig uses here are too short to vouch for anything. See <see cref="ConfiguredFolder"/>.
    /// </summary>
    public string? ResolveRoot() => Resolve().Root;

    /// <summary>
    /// The cache root. <see cref="Outputs"/> is refused here although the plan removes it, because the
    /// plan removes it only with its index, and Explore removes one folder at a time.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        ResolveRoot() is { } root
            ?
            [
                new ToolRoot(
                    root,
                    "This is Zig's build cache. Deguffer removes what Zig compiled from inside it, and leaves "
                    + "the packages Zig fetched. The build outputs go only together with the records that "
                    + "point at them, so they are removed from the preview rather than here.",
                    RemovableAlone.Contains),
            ]
            : [];

    /// <summary>
    /// Presence is a cache actually on disk, never the root existing: a root holding only fetched
    /// packages has nothing this row would offer.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveRoot() is { } root
            && Children.DisposableNames.Any(name => LongPath.DirectoryMayExist(Path.Combine(root, name))));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var setting = Resolve();

        if (setting.Root is not { } root)
        {
            return EmptyPlan(
                $"{CacheVariable} is set to '{setting.Value}', and Deguffer will not treat that as Zig's cache: "
                + $"{setting.Declined} It is leaving it alone.");
        }

        if (NothingToPlanFor(root, $"Zig has not built anything for this user — no {root} directory.") is { } nothing)
        {
            return nothing;
        }

        // The root arrives by name, from an environment variable or a default, so nothing has classified
        // it. A junctioned root hands back the far side's ordinary directories, and a recognised name
        // among them would be targeted while every survivor named for this root resolved through the
        // same link and passed.
        if (LongPath.IsReparsePoint(root))
        {
            return UnexaminedPlan(
                $"Leaving '{root}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var notes = new List<PlanNote>();
        var declined = new List<(string Path, string Reason)>();
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var scan = ChildDirectories.Under(root);

        // The root was found on disk above, and a listing right is separate from a traverse right — so a
        // refusal here leaves a plan with no steps and, without this, nothing said.
        if (scan.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(root));
        }

        foreach (var link in scan.Links)
        {
            Decline(LongPath.Display(link.FullName), link.Name, LinkReason);
        }

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var classification = Children.Classify(child.Name);
            var path = LongPath.Display(child.FullName);

            if (classification.Tier.IsOfferable())
            {
                found[child.Name] = path;
            }
            else
            {
                Decline(path, child.Name, classification.Reason);
            }
        }

        var targets = Targets(
            found,
            indexIsLink: scan.Links.Any(link => link.Name.Equals(Index, StringComparison.OrdinalIgnoreCase)),
            Decline);

        if (targets.Count == 0 && declined.Count == 0 && !scan.Unreadable)
        {
            return EmptyPlan($"Zig has built nothing into {root} yet.");
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

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
            ProtectedPaths = Protect(
            [
                (root, "Zig's cache folder itself must survive — only what Zig compiled inside it is removed."),
                .. declined,
            ]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = scan.Unreadable,
            WasNotExamined = targets.Count == 0 && declined.Count > 0,
        };

        void Decline(string path, string name, string reason)
        {
            notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Leaving '{name}' alone: {reason}"));
            declined.Add((path, reason));
        }
    }

    /// <summary>
    /// One target per recognised child, but the outputs and their index are one.
    ///
    /// <para><b>The outputs are offered only where the index is an ordinary directory.</b> An index that
    /// is a link is left alone, and Zig reads its manifests through the link, so the outputs they name
    /// have to stay as well. Outputs with no index at all are declined too, for a different reason: a
    /// build between the preview and the clean would write an index the step knows nothing about, and
    /// the next preview, once Zig has built anything, offers both.</para>
    /// </summary>
    /// <param name="indexIsLink">Whether the index was found as a link, which says which reason applies.</param>
    private List<DeletionTarget> Targets(
        Dictionary<string, string> found,
        bool indexIsLink,
        Action<string, string, string> decline)
    {
        var targets = new List<DeletionTarget>(found.Count);

        foreach (var (name, path) in found)
        {
            if (name.Equals(Outputs, StringComparison.OrdinalIgnoreCase))
            {
                if (found.TryGetValue(Index, out var index))
                {
                    targets.Add(new DeletionTarget(
                        path,
                        Children.Classify(name).Reason,
                        IndexedBy: [index],
                        UseCheck: new RunningProcessCheck(Inspector, BuildingProcessNames)));
                }
                else
                {
                    decline(
                        path,
                        name,
                        indexIsLink
                            ? $"Zig reads its records of what it built through the link '{Index}', and the build "
                              + "outputs go only together with those records."
                            : $"Zig's records of what it built, in '{Index}', are missing. A build before the clean "
                              + "would write new records that point into these outputs, so they are offered once "
                              + "Zig has written its records again.");
                }

                continue;
            }

            // The index goes with the outputs where there are any, and on its own where there are none.
            if (name.Equals(Index, StringComparison.OrdinalIgnoreCase) && found.ContainsKey(Outputs))
            {
                continue;
            }

            targets.Add(new DeletionTarget(path, Children.Classify(name).Reason));
        }

        return targets;
    }

    private ZigCacheSetting Resolve()
    {
        var value = Environment.GetEnvironmentVariable(CacheVariable)?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return new ZigCacheSetting(null, DefaultRoot, null);
        }

        if (LongPath.Configured(value) is not { } configured)
        {
            return new ZigCacheSetting(value, null, "it is not a full path, so Deguffer cannot tell which directory it means.");
        }

        return ConfiguredFolder.WhyNotOwned(configured, Environment, _system, TempRoots.Resolve(Environment, _system).AccountFolders) is { } declined
            ? new ZigCacheSetting(value, null, declined)
            : new ZigCacheSetting(value, configured, null);
    }

    /// <param name="Value">What <see cref="CacheVariable"/> holds, or null where it is not set.</param>
    /// <param name="Root">The cache folder to examine, or null where it is declined.</param>
    /// <param name="Declined">Why it is declined, as the end of a sentence, or null where it is not.</param>
    private readonly record struct ZigCacheSetting(string? Value, string? Root, string? Declined);
}
