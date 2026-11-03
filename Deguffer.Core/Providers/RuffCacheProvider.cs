using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A project's <c>.ruff_cache</c>: ruff's lint and format results per file, kept so the next run
/// reads only what changed.
///
/// <para><b>Recognised by both files ruff writes, read.</b> ruff's tag carries no line of its own:
/// it is the 43-byte header and nothing else, from the <c>cachedir</c> crate's <c>ensure_tag</c>. So
/// the tag alone cannot say ruff wrote it, and what does is the <c>.gitignore</c> ruff writes beside
/// it in the same step, "# Automatically created by ruff." and <c>*</c>
/// (<c>init</c> in <see href="https://github.com/astral-sh/ruff/blob/main/crates/ruff/src/cache.rs">cache.rs</see>).
/// Both are required whole, as ruff writes them. ruff puts the folder in the project root that holds
/// its configuration, <c>.ruff.toml</c>, <c>ruff.toml</c> or a <c>pyproject.toml</c>, so one must
/// sit beside it. A cache moved by <c>--cache-dir</c>, <c>RUFF_CACHE_DIR</c> or <c>cache-dir</c> is
/// not found.</para>
///
/// <para><b>Tier 1.</b> Nothing inside is anything but ruff's own results, and the next run makes
/// them again from the source.</para>
/// </summary>
public sealed class RuffCacheProvider : BuildDirectoryProvider
{
    private static readonly BuildDirectoryKind RuffCache = new()
    {
        DirectoryNames = [".ruff_cache"],
        RequiredMarkers =
        [
            MarkerFile.BareCacheDirectoryTag,
            new MarkerFile(".gitignore", ["# Automatically created by ruff.", "*"], IsWholeFile: true),
        ],
        AnyOfSiblings = [".ruff.toml", "ruff.toml", "pyproject.toml"],
    };

    public RuffCacheProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null)
        : base(RuffCache, roots, discovery, liveTrees, environment, runner, inspector, scanner, volumes)
    {
    }

    public override string Id => "ruff-cache";

    public override string Name => "ruff caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The next ruff run lints and formats every file rather than only what changed, so that one " +
        "run is slower. ruff makes the folder again as it goes.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "ruff, the Python linter and formatter",
        Publisher = "Astral",
        Purpose = "ruff keeps a .ruff_cache folder in each project it checks, holding its results "
            + "for every file so the next run reads only what changed.",
        Recommendation = "Deguffer offers one only where ruff's own cache tag and the .gitignore it "
            + "writes are inside it and a file ruff reads its settings from sits beside it, and only "
            + "inside the folders listed under Settings, Source folders.",
    };

    protected override string Subject => "a ruff cache";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
        "ruff caches inside them, and nowhere else.";
}
