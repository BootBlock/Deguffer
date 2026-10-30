using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A project's <c>.pytest_cache</c>: what pytest's cache plugin remembers between runs, chiefly
/// which tests failed last.
///
/// <para><b>Recognised by the tag pytest writes, read.</b> pytest creates the folder in one step,
/// with <c>CACHEDIR.TAG</c>, a <c>.gitignore</c> and a <c>README.md</c> inside, and only when the
/// folder does not exist (<c>_make_cachedir</c> in
/// <see href="https://github.com/pytest-dev/pytest/blob/main/src/_pytest/cacheprovider.py">cacheprovider.py</see>).
/// The tag's second line, "created by pytest", is what tells pytest's folder from one somebody tagged
/// by hand. The folder sits in pytest's root directory, which is the folder holding the configuration
/// file pytest found, so one of the files pytest reads its configuration from must sit beside it
/// (<see href="https://github.com/pytest-dev/pytest/blob/main/src/_pytest/config/findpaths.py">findpaths.py</see>).
/// A cache moved by <c>cache_dir</c> is not beside one, and is not found.</para>
///
/// <para><b>Tier 1.</b> Everything inside is something pytest records as it runs, and pytest makes
/// the folder again on its next run. What goes is the record behind <c>--lf</c> and <c>--ff</c>,
/// and anything a test stored through the <c>cache</c> fixture.</para>
/// </summary>
public sealed class PytestCacheProvider : BuildDirectoryProvider
{
    private static readonly BuildDirectoryKind PytestCache = new()
    {
        DirectoryNames = [".pytest_cache"],
        RequiredMarkers = [MarkerFile.CacheDirectoryTag("# This file is a cache directory tag created by pytest.")],
        AnyOfSiblings = ["pytest.toml", ".pytest.toml", "pytest.ini", ".pytest.ini", "pyproject.toml", "tox.ini", "setup.cfg"],
    };

    public PytestCacheProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null)
        : base(PytestCache, roots, discovery, liveTrees, environment, runner, inspector, scanner, volumes)
    {
    }

    public override string Id => "pytest-cache";

    public override string Name => "pytest caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "pytest makes the folder again the next time it runs. Until a run has finished, --lf and --ff " +
        "have no record of which tests failed, so they run everything, and values a test stored " +
        "through the cache fixture are computed again.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.ProjectBuildOutput,
        Application = "pytest, the Python testing tool",
        Publisher = "the pytest project",
        Purpose = "pytest keeps a .pytest_cache folder in each project it runs in, recording which "
            + "tests failed last time and anything a test asked it to remember.",
        Recommendation = "Deguffer offers one only where pytest's own cache tag is inside it and a "
            + "file pytest reads its settings from sits beside it, and only inside the folders "
            + "listed under Settings, Source folders.",
    };

    protected override string Subject => "a pytest cache";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
        "pytest caches inside them, and nowhere else.";
}
