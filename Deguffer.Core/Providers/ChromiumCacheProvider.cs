using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The Chromium caches inside desktop applications that embed the engine (~0.8 GB across ten
/// applications on the audited machine, and a published 2 to 5 GB for one heavily used chat client),
/// and inside the Chromium-based browsers themselves (910 MB of user data for one browser on the
/// same machine).
///
/// <para>Almost no cleaner handles the applications that ship Chromium inside themselves, each
/// writing the same fixed set of cache directory names under its own vendor name. That is what makes
/// this recognisable by shape rather than by name: the directory names belong to Chromium, not to
/// the vendor, so one provider reaches an unbounded set of applications without knowing any of
/// them. A browser is the same shape in a place that has to be named, because it keeps its folder
/// below a vendor directory and a product directory — see <see cref="ChromiumHost"/>.</para>
///
/// <para><b>The signature is an exact allow-list of nine names</b>, every one of them derived
/// content the engine rebuilds without being asked. How a folder is identified, what is asserted to
/// survive beside the nine, and when a folder is in use are shared with the service-worker row and
/// explained on <see cref="ChromiumUserDataProvider"/>.</para>
///
/// <para><b><c>Service Worker\CacheStorage</c> is not one of them.</b> It is what a site's service
/// worker stored so the site works offline, and Chromium counts it as site data: its own "Clear
/// browsing data" removes it with cookies, not with cached files. A web application that was used
/// offline stops working offline until it is next online, and anything it kept only there does not
/// come back. That is not "a slower next use and nothing else", so it is a Tier 2 row of its own —
/// <see cref="ChromiumServiceWorkerStorageProvider"/>.</para>
/// </summary>
public sealed class ChromiumCacheProvider : ChromiumUserDataProvider
{
    /// <summary>
    /// Chromium's nine cache directories, grouped by the directory each sits in. Anything not named
    /// here is Tier 4 by construction — which is what makes "we did not recognise that" fail closed
    /// beside data that would be gone for good.
    ///
    /// <para><c>Cache</c> and <c>Service Worker</c> appear as Tier 4 entries rather than as
    /// omissions, because they are the cases where the unrecognised-child reason would be actively
    /// misleading. <c>Cache</c> really is left standing while the cache inside it is removed, and
    /// <c>Service Worker</c> is recognised: the offline storage inside it is offered on another
    /// row.</para>
    /// </summary>
    public static readonly IReadOnlyList<CacheLevel> Levels =
    [
        new CacheLevel(string.Empty, new DisposableChildSet(
        [
            new ChildClassification(
                "Code Cache",
                SafetyTier.RegenerableCache,
                "Compiled JavaScript and WebAssembly. The application recompiles each script the first time it runs again."),
            new ChildClassification(
                "GPUCache",
                SafetyTier.RegenerableCache,
                "Compiled graphics pipelines. The application rebuilds them on demand."),

            // The engine's own shader caches, beside GPUCache and written by the same GPU process:
            // GrShaderCache for the Skia renderer, ShaderCache for the ANGLE layer beneath it. Edge
            // keeps GrShaderCache in its user-data folder beside the profiles rather than in one,
            // which the walk reaches because that folder is always the first profile it visits.
            new ChildClassification(
                "GrShaderCache",
                SafetyTier.RegenerableCache,
                "Compiled graphics shaders. The application rebuilds them on demand."),
            new ChildClassification(
                "ShaderCache",
                SafetyTier.RegenerableCache,
                "Compiled graphics shaders. The application rebuilds them on demand."),
            new ChildClassification(
                "DawnGraphiteCache",
                SafetyTier.RegenerableCache,
                "Compiled WebGPU pipelines. The application rebuilds them on demand."),
            new ChildClassification(
                "DawnWebGPUCache",
                SafetyTier.RegenerableCache,
                "Compiled WebGPU pipelines. The application rebuilds them on demand."),

            // A separate directory from DawnGraphiteCache, not a misspelling of it: the engine writes
            // both names, and a user-data folder was measured holding this one beside GPUCache.
            new ChildClassification(
                "GraphiteDawnCache",
                SafetyTier.RegenerableCache,
                "Compiled graphics pipelines. The application rebuilds them on demand."),

            // Dawn is the engine's WebGPU implementation, and this is the name older builds gave its
            // pipeline cache. Battle.net's launcher still writes it, in the same disk-cache format as
            // GPUCache: an index and its data files.
            new ChildClassification(
                "DawnCache",
                SafetyTier.RegenerableCache,
                "Compiled WebGPU pipelines, from an older build of the engine. The application rebuilds them on demand."),
            new ChildClassification(
                "Cache",
                SafetyTier.DoNotTouch,
                "The directory the web cache sits in. Only the 'Cache_Data' inside it is removed, and the directory "
                + "holding it stays, so anything that ever appears beside the cache is left alone."),
            new ChildClassification(
                "Service Worker",
                SafetyTier.DoNotTouch,
                "Service-worker registrations and scripts, and what the sites stored to work offline. "
                + "That offline storage is offered on a row of its own, because removing it costs more "
                + "than a slower start."),
        ])),
        new CacheLevel("Cache", new DisposableChildSet(
        [
            new ChildClassification(
                "Cache_Data",
                SafetyTier.RegenerableCache,
                "Web content the application saved so it would not fetch the same thing twice. It is downloaded again when it is next wanted."),
        ])),
    ];

    public ChromiumCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ILiveTreeInspector? liveTrees = null,
        ChromiumUserDataDiscovery? discovery = null)
        : base(environment, runner, inspector, scanner, liveTrees, discovery)
    {
    }

    public override string Id => "chromium-app-cache";

    public override string Name => "Chromium application caches";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "Each application fetches the web content it had cached and recompiles its scripts the " +
        "first time it is opened again, so it starts more slowly once. Sign-ins, saved passwords " +
        "and settings are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Chromium-based browsers — Chrome, Edge, Brave, Vivaldi and Opera — and "
            + "the desktop applications that embed the same engine: chat clients, editors and "
            + "other Electron apps, the applications that show web content through Microsoft's "
            + "WebView2, and the Battle.net launcher",
        Publisher = "each application's own vendor; the cache format belongs to the Chromium "
            + "project",
        Purpose = "A Chromium browser caches web content, compiled scripts and GPU shaders under its "
            + "own folder in your profile, and an application built on Chromium does exactly the "
            + "same under its own. Almost no cleaner reaches the applications, so their caches grow "
            + "unnoticed across every such application on the machine.",
        Recommendation = "Deguffer removes nine cache directories whose names belong to Chromium "
            + "itself, and leaves everything else in the folder alone — the sign-ins, saved "
            + "passwords, saved payment cards and offline data sit right beside them. What sites "
            + "stored to work offline is a separate row, because removing it costs more than a "
            + "slower start.",
    };

    protected override IReadOnlyList<CacheLevel> CacheLevels => Levels;

    protected override string NothingFound =>
        "No application on this machine keeps a Chromium cache in its data folder.";

    protected override string WhatIsRemoved => "the caches";

    protected override string ContainerSentence =>
        "'Cache_Data' sits inside a directory of its own, 'Cache', and that directory stays: only the "
        + "cache inside it is removed.";
}
