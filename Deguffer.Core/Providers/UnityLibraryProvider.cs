using Deguffer.Core.Configuration;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A Unity project's <c>Library</c> folder — the imported form of everything in <c>Assets</c>.
///
/// <para><b>Tier 2, and this time the survey's argument survives the re-derivation.</b> Nothing in
/// <c>Library</c> is anybody's only copy: it is the artefact database, the shader and Burst caches,
/// the script assemblies and the resolved package cache, all built from <c>Assets</c>,
/// <c>Packages</c> and <c>ProjectSettings</c>, which is why every Unity <c>.gitignore</c> template
/// excludes it and Unity's own documentation says to delete it when it misbehaves. What makes it
/// Tier 2 rather than Tier 1 is the price of getting it back: reopening the project reimports every
/// asset, which on a large one is tens of minutes rather than a slower build, and
/// <c>Library\PackageCache</c> is fetched over the network again. Tier 2's definition is
/// "re-downloading gigabytes <em>or</em> re-indexing for minutes", and this is both.</para>
///
/// <para>It does hold a few editor preferences — which scenes were last open, which build target is
/// selected, which inspector nodes are expanded. Those are settings Unity rewrites, not a record of
/// anything that happened, which is the line §3 draws around Tier 3. Visual Studio's <c>.vs</c> is
/// the contrasting case in the same phase, and it falls the other side of it.</para>
///
/// <para>Recognition is a content signature over the <em>parent</em>: a <c>Library</c> is Unity's
/// when <c>Assets</c>, <c>Packages</c> and <c>ProjectSettings</c> sit beside it. On its own the name
/// is an ordinary English word, and a directory called <c>Library</c> is as likely to hold somebody's
/// sample collection.</para>
/// </summary>
public sealed class UnityLibraryProvider : BuildDirectoryProvider
{
    /// <summary>
    /// <c>UnityLockfile</c> is Unity's own answer to "is this project open", written when the editor
    /// opens a project and removed when it closes. Its <em>existence</em> is not the test — a
    /// crashed editor leaves one behind for ever — so it is handed to the live-tree inspector, which
    /// asks whether anything holds it open.
    /// </summary>
    private static readonly BuildDirectoryKind UnityLibrary = new()
    {
        DirectoryNames = ["Library"],
        RequiredSiblings = ["Assets", "Packages", "ProjectSettings"],
        LockFiles = ["UnityLockfile"],
    };

    public UnityLibraryProvider(
        SourceRootStore roots,
        SourceDirectoryDiscovery? discovery = null,
        ILiveTreeInspector? liveTrees = null,
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(UnityLibrary, roots, discovery, liveTrees, environment, runner, inspector, scanner)
    {
    }

    public override string Id => "unity-library";

    public override string Name => "Unity project library";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next time you open the project, Unity reimports every asset and rebuilds its caches. " +
        "On a large project that takes many minutes, and any packages it had downloaded are fetched " +
        "again. Nothing is lost — all of it is derived from Assets, Packages and ProjectSettings.";

    protected override string Subject => "a Unity project's imported assets and caches";

    protected override string NothingApprovedGuidance =>
        "No source folders have been added yet. Add them in Settings and Deguffer will look for " +
        "Unity projects inside them, and nowhere else.";
}
