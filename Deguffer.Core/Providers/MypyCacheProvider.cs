using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A project's <c>.mypy_cache</c>: mypy's record of every module it has checked, kept so the next
/// check reads only what changed.
///
/// <para><b>Recognised by the tag mypy writes, read.</b> At the end of every run mypy writes
/// <c>CACHEDIR.TAG</c> into its cache folder where it is missing, with the line "automatically created
/// by mypy", and a <c>.gitignore</c> beside it
/// (<c>exclude_from_backups</c> in <see href="https://github.com/python/mypy/blob/master/mypy/build.py">build.py</see>).
/// It writes through Python's text mode, so on Windows each line ends in <c>\r\n</c>, which
/// <see cref="MarkerFile"/> accepts. mypy puts the folder in the directory it runs from, which is
/// ordinarily the project holding its configuration: <c>mypy.ini</c>, <c>.mypy.ini</c>, or a
/// <c>pyproject.toml</c> or <c>setup.cfg</c> with a mypy section (<c>CONFIG_NAMES</c> and
/// <c>SHARED_CONFIG_NAMES</c> in
/// <see href="https://github.com/python/mypy/blob/master/mypy/defaults.py">defaults.py</see>). One
/// must sit beside it. A cache moved by <c>--cache-dir</c> or <c>MYPY_CACHE_DIR</c> is not found.</para>
///
/// <para><b>Tier 1.</b> Nothing inside is anything but mypy's own results, and the next check makes
/// them again from the source.</para>
/// </summary>
public sealed class MypyCacheProvider : BuildDirectoryProvider
{
    private static readonly BuildDirectoryKind MypyCache = new()
    {
        DirectoryNames = [".mypy_cache"],
        RequiredMarkers = [MarkerFile.CacheDirectoryTag("# This file is a cache directory tag automatically created by mypy.")],
        AnyOfSiblings = ["mypy.ini", ".mypy.ini", "pyproject.toml", "setup.cfg"],
    };

    public MypyCacheProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null)
        : base(MypyCache, roots, discovery, liveTrees, environment, runner, inspector, scanner, volumes)
    {
    }

    public override string Id => "mypy-cache";

    public override string Name => "mypy caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The next mypy run checks every module from the start rather than only what changed, so " +
        "that one run is slower. mypy makes the folder again as it goes.";

    public override ProviderBasis Basis => ProviderBasis.Researched;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "mypy, the static type checker for Python",
        Publisher = "the mypy project",
        Purpose = "mypy keeps a .mypy_cache folder in each project it checks, holding its results "
            + "for every module so the next check reads only what changed.",
        Recommendation = "Deguffer offers one only where mypy's own cache tag is inside it and a "
            + "file mypy reads its settings from sits beside it, and only inside the folders listed "
            + "under Settings, Source folders.",
    };

    protected override string Subject => "a mypy cache";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
        "mypy caches inside them, and nowhere else.";
}
