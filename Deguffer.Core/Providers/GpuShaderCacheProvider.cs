using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Compiled shader pipelines written by the graphics drivers (3.2 GB on the audited machine, almost
/// all of it NVIDIA's <c>DXCache</c>).
///
/// The purest Tier 1 on the disk. The driver keys these blobs by its own version and discards them
/// itself whenever that version changes, so they are regenerated transparently and the only cost of
/// deleting one is a few seconds of stutter the first time a scene renders. §5.1 does not apply:
/// no vendor ships a cache-eviction command, and every published instruction is to delete the
/// directory.
///
/// <para><b>One provider over four locations, not one per vendor.</b> Every vendor's shader cache
/// is the same fact — driver-version-keyed pipeline blobs, rebuilt on demand — so the tier, the
/// sentence the user reads and the reasoning behind both are identical. Four classes would be four
/// copies of one piece of knowledge, and the copies would drift. What actually differs is which
/// directory and which child names, and that is data: <see cref="Roots"/> is the table, and each
/// row carries its own <see cref="DisposableChildSet"/> so §5.2 stays answerable from one
/// declaration. The user keeps per-vendor control regardless, because selection is per step and
/// each cache is its own step.</para>
///
/// <para>§5.2 is live here rather than theoretical: <c>%LOCALAPPDATA%\NVIDIA</c> holds
/// <c>accounts</c> — NVIDIA sign-in state — beside the two caches, so the root is never a target.
/// It was observed as a <em>file</em>, which is the reason each root declares protected names
/// separately from its disposable children: a child set classifies directories, and a file in the
/// root is never enumerated, so naming it is the only way §5.6 ever asserts it survived. It is the
/// same shape as Gradle's <c>gradle.properties</c>.</para>
///
/// <para>Alone among the path-based providers this one raises no §5.3 running-process warning, and
/// the omission is deliberate rather than forgotten. Nothing owns a shader cache: the driver writes
/// it from inside whichever application is rendering, so there is no process to name and a warning
/// would have to name one. A blob held open is skipped by the remover and rebuilt on demand, which
/// is the same outcome as deleting it.</para>
/// </summary>
public sealed class GpuShaderCacheProvider : CleanupProviderBase
{
    /// <summary>
    /// What may be deleted under each vendor's directory. Anything not named here is Tier 4 by
    /// construction, which is the direction §5.2 requires the unknown case to fail in.
    ///
    /// AMD is deliberately thin. <c>DxCache</c> is the child the survey documented, and no AMD
    /// machine was available to establish what else that root holds — so the other names sometimes
    /// attributed to it are absent rather than guessed at. The consequence is an incomplete reclaim
    /// on an AMD machine, which is the safe direction to be wrong in.
    /// </summary>
    public static readonly IReadOnlyList<ShaderCacheRoot> Roots =
    [
        new ShaderCacheRoot("NVIDIA", new DisposableChildSet(
        [
            new ChildClassification(
                "DXCache",
                SafetyTier.RegenerableCache,
                "Compiled Direct3D shader pipelines. The driver rebuilds each one the first time it is needed again."),
            new ChildClassification(
                "GLCache",
                SafetyTier.RegenerableCache,
                "Compiled OpenGL and Vulkan program binaries. The driver rebuilds them on demand."),
        ]),
        [("accounts", "NVIDIA account and sign-in state. It sits beside the caches and is not one.")]),
        new ShaderCacheRoot("AMD", new DisposableChildSet(
        [
            new ChildClassification(
                "DxCache",
                SafetyTier.RegenerableCache,
                "Compiled Direct3D shader pipelines. The driver rebuilds each one the first time it is needed again."),
        ]), []),
        new ShaderCacheRoot("Intel", new DisposableChildSet(
        [
            new ChildClassification(
                "ShaderCache",
                SafetyTier.RegenerableCache,
                "Compiled shader pipelines. The driver rebuilds each one the first time it is needed again."),
        ]), []),
    ];

    /// <summary>
    /// Direct3D's own shader cache, which the OS writes rather than any one vendor's driver.
    ///
    /// The only whole-directory target this provider has, and the reason is that it has no tool root
    /// to enumerate: its parent is <c>%LOCALAPPDATA%</c>, which is the profile rather than a tool's
    /// directory. Everything inside it is Direct3D's, arriving as opaque per-application containers
    /// whose names prove nothing, so a recognised-child rule there would recognise none of them.
    /// §5.2's substance still holds — the parent is never targeted and never enumerated, and it is
    /// what §5.6 asserts survived.
    /// </summary>
    public const string Direct3DCacheName = "D3DSCache";

    private readonly string _direct3DCache;

    public GpuShaderCacheProvider(
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
        _direct3DCache = Path.Combine(Environment.LocalAppData, Direct3DCacheName);
    }

    public override string Id => "gpu-shader-cache";

    public override string Name => "GPU shader caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The driver recompiles each shader the first time it is wanted again, so a game or 3D " +
        "application stutters briefly on its next run and then behaves exactly as before.";

    /// <summary>The vendor root paths on this machine. Exposed so tests can assert none is targeted.</summary>
    public IReadOnlyList<string> RootPaths =>
        [.. Roots.Select(r => Path.Combine(Environment.LocalAppData, r.DirectoryName))];

    /// <summary>
    /// Presence is a cache actually on disk, never a vendor directory existing.
    /// <c>%LOCALAPPDATA%\Intel</c> is present on machines with no Intel graphics cache at all, and
    /// treating that as a hit would report a source the plan then has nothing to say about.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(RecognisedCachePaths().Any(LongPath.DirectoryExists));

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();
        var survivors = new List<(string Path, string Reason)>();

        foreach (var root in Roots)
        {
            ct.ThrowIfCancellationRequested();

            var rootPath = Path.Combine(Environment.LocalAppData, root.DirectoryName);

            if (!LongPath.DirectoryExists(rootPath))
            {
                continue;
            }

            survivors.Add((
                rootPath,
                $"The {root.DirectoryName} directory itself must survive — only its known-disposable children are removed."));

            survivors.AddRange(root.ProtectedNames.Select(p => (Path.Combine(rootPath, p.Name), p.Reason)));

            CollectFrom(root, rootPath, targets, declined, notes, ct);
        }

        if (LongPath.DirectoryExists(_direct3DCache))
        {
            // Reached by name rather than through ChildDirectories.Under, so the reparse check that
            // protects every other target has to be made here. Redirecting a shader cache to another
            // volume with a junction is common, and a plan naming this path while deleting whatever
            // it points at is the §5.2 failure in its worst form: the user approved one tree and
            // another went.
            if (LongPath.IsReparsePoint(_direct3DCache))
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{Direct3DCacheName}' alone: it is a link to somewhere else, and Deguffer " +
                    "does not delete through a link."));
                declined.Add((
                    _direct3DCache,
                    "A link rather than a directory, so what it points at was never classified."));
            }
            else
            {
                targets.Add(new DeletionTarget(
                    _direct3DCache,
                    "Direct3D's own compiled shader cache. Windows re-creates it and the driver refills it on demand."));
            }
        }

        if (targets.Count == 0 && declined.Count == 0)
        {
            return EmptyPlan("No graphics driver has written a shader cache for this user.");
        }

        // The parent of every target. A weak assertion on its own — an over-broad rule that emptied
        // the profile would leave the directory standing — but it is the only one a whole-directory
        // target under a shared parent admits, and the vendor roots above are its siblings, so on
        // any machine with a vendor cache the negative has real subjects too.
        survivors.Add((
            Environment.LocalAppData,
            "The profile's local application data must survive — only named shader caches inside it are removed."));

        var (steps, measured) = await PlanDeletionsAsync(targets, ct).ConfigureAwait(false);

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
            ProtectedPaths = Protect([.. survivors, .. declined]),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// §5.2 for one vendor root: classify every child, target the recognised ones, and say plainly
    /// what is being left behind. A declined child is protected by name as well as omitted, because
    /// the declined and the targeted are siblings under one parent — which is exactly when an
    /// over-broad rule takes both.
    /// </summary>
    private static void CollectFrom(
        ShaderCacheRoot root,
        string rootPath,
        List<DeletionTarget> targets,
        List<(string Path, string Reason)> declined,
        List<PlanNote> notes,
        CancellationToken ct)
    {
        foreach (var child in ChildDirectories.Under(rootPath))
        {
            ct.ThrowIfCancellationRequested();

            var classification = root.Children.Classify(child.Name);
            var path = LongPath.Display(child.FullName);

            if (!classification.Tier.IsOfferable())
            {
                // Qualified by vendor: two roots may hold a child of the same name, and an
                // unqualified note would leave the user unable to tell which one it meant.
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{root.DirectoryName}\\{child.Name}' alone: {classification.Reason}"));
                declined.Add((path, classification.Reason));
                continue;
            }

            targets.Add(new DeletionTarget(path, classification.Reason));
        }
    }

    /// <summary>
    /// Every path this provider could ever target, by declaration rather than by enumeration —
    /// so answering "is there anything here?" costs one existence check each and can never reach a
    /// child the table does not name.
    /// </summary>
    private IEnumerable<string> RecognisedCachePaths()
    {
        foreach (var root in Roots)
        {
            foreach (var child in root.Children.DisposableNames)
            {
                yield return Path.Combine(Environment.LocalAppData, root.DirectoryName, child);
            }
        }

        yield return _direct3DCache;
    }
}
