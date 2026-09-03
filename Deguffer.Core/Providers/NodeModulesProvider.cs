using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A JavaScript project's <c>node_modules</c> — installed dependencies, reinstalled from a lock file.
///
/// <para><b>A lock file beside it is required, and that is the whole of the safety argument.</b>
/// "Regenerable" is a claim that reinstalling produces what was there, and only a lock file makes
/// that true: without one, <c>npm install</c> re-resolves version ranges and can produce a different
/// tree, which is not regeneration but a change to the project. So a <c>node_modules</c> whose
/// project has no lock file is declined. That leaves space unreclaimed, which is the direction §5.2
/// says to err in.</para>
///
/// <para>Tier 2 for the obvious half of its definition: reinstalling is a download, and on a project
/// of any size it is a large one. It is offline where the package manager's own cache still holds
/// the tarballs, which is exactly the cache <c>NpmCacheProvider</c> and <c>PnpmStoreProvider</c>
/// clear — so a run that takes both leaves a reinstall needing the network.</para>
///
/// <para>This is also the directory <see cref="SourceTreeBoundary"/> refuses to walk into, and the
/// two rules meet without conflicting: a search stops at a name it is looking for, so finding one
/// costs a single directory entry rather than the hundreds of thousands of files beneath it, and a
/// <c>node_modules</c> nested inside another belongs to its parent and is never offered separately.</para>
/// </summary>
public sealed class NodeModulesProvider : BuildDirectoryProvider
{
    private static readonly BuildDirectoryKind NodeModules = new()
    {
        DirectoryNames = ["node_modules"],
        RequiredSiblings = ["package.json"],
        AnyOfSiblings = ["package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb"],
    };

    public NodeModulesProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(NodeModules, roots, discovery, liveTrees, environment, runner, inspector, scanner)
    {
    }

    public override string Id => "node-modules";

    public override string Name => "Node.js project dependencies";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The project will not build or run until its dependencies are installed again. The lock file " +
        "beside it pins the exact versions, so what comes back is what was there. It is fetched from " +
        "your package manager's cache where that still holds it, and from the network where it does " +
        "not.";

    /// <summary>
    /// Whose files these are and what they are for. See <see cref="ProviderDescription"/>.
    /// </summary>
    public override ProviderDescription Description { get; } = new()
    {
        Application = "npm, pnpm or yarn — whichever package manager installed the project",
        Publisher = "each package manager's own publisher; Node.js itself is stewarded by the "
            + "OpenJS Foundation",
        Purpose = "A JavaScript project's node_modules holds every dependency it was installed "
            + "with, unpacked and ready to run. It is routinely both the largest directory in a "
            + "project and the one with the most files in it.",
        Recommendation = "Only when you need the space, and only for a project you are not "
            + "working on: it will not build or run until its dependencies are installed again. "
            + "Deguffer offers one only where a lock file sits beside it, because without a lock "
            + "file a reinstall can produce a different tree.",
    };

    protected override string Subject => "installed Node.js dependencies";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
        "Node.js projects inside them, and nowhere else.";
}
