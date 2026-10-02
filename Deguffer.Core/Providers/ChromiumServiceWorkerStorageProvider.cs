using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What sites' service workers stored for offline use, in every Chromium user-data folder: the
/// <c>Service Worker\CacheStorage</c> directory of each profile of each browser and of each
/// application that embeds the engine.
///
/// <para><b>Tier 2, not Tier 1.</b> Cache Storage is the store a service worker fills so its site
/// loads and works without a connection. Chromium counts it as site data: its own "Clear browsing
/// data" removes it with "Cookies and other site data", not with "Cached images and files", and the
/// browser evicts it by itself only under storage pressure, a whole site at a time. Removing it keeps
/// the service worker registered, because <c>Service Worker\Database</c> stays, but its stored
/// responses are gone: a web application that worked offline does not work offline again until it has
/// been online once. What the server still has comes back then, and anything a site kept only here
/// does not. That is a real cost beyond a slower start, so §3 places it in Tier 2, offered and never
/// pre-selected.</para>
///
/// <para>A separate row from <see cref="ChromiumCacheProvider"/> rather than a Tier 2 entry in its
/// table, because a plan carries its provider's tier: a child declared at Tier 2 in a Tier 1 row
/// would still be pre-selected. Everything else is the same and is shared through
/// <see cref="ChromiumUserDataProvider"/>.</para>
///
/// <para>VS Code's webview partitions and the Epic Games launcher's store keep a directory of the same
/// name, and their rows offer it at Tier 1. There it holds what an editor view or the store's own
/// pages fetched. Losing it costs those pages a slower load, not a web application someone relies on
/// offline: an editor view loads its content from the editor, and the store sells nothing without a
/// connection.</para>
/// </summary>
public sealed class ChromiumServiceWorkerStorageProvider : ChromiumUserDataProvider
{
    /// <summary>
    /// The one directory this row removes, and the directory it sits in. Anything not named here is
    /// Tier 4 by construction.
    ///
    /// <para><c>Service Worker</c> is a Tier 4 entry rather than an omission, because it is the case
    /// where the unrecognised-child reason would be actively misleading: the directory really is left
    /// standing, and something inside it really is being removed. Its other children, the
    /// <c>Database</c> that registers each worker and the <c>ScriptCache</c> that holds its code, are
    /// absent and so spared: a worker that is still registered and still has its script is what
    /// refills the storage the next time its site is opened.</para>
    /// </summary>
    public static readonly IReadOnlyList<CacheLevel> Levels =
    [
        new CacheLevel(string.Empty, new DisposableChildSet(
        [
            new ChildClassification(
                "Service Worker",
                SafetyTier.DoNotTouch,
                "Service-worker registrations and scripts, next to what they stored for offline use. "
                + "Only the 'CacheStorage' inside it is removed, so every service worker stays registered."),
        ])),
        new CacheLevel("Service Worker", new DisposableChildSet(
        [
            new ChildClassification(
                "CacheStorage",
                SafetyTier.RegenerableWithCost,
                "Responses sites' service workers stored so they would work offline. A site fetches "
                + "them again the next time it is opened online, and does not work offline until then."),
        ])),
    ];

    public ChromiumServiceWorkerStorageProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ILiveTreeInspector? liveTrees = null,
        ChromiumUserDataDiscovery? discovery = null,
        RowDeclarations? declarations = null)
        : base(environment, runner, inspector, scanner, liveTrees, discovery, declarations)
    {
    }

    public override string Id => "chromium-service-worker-storage";

    public override string Name => "Chromium offline site storage";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "A site or web application that worked offline does not work offline again until it has been " +
        "opened online once, when it fetches what it had stored. Anything a site kept only in this " +
        "storage is gone. Sign-ins, saved passwords and the sites' service workers are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Chromium-based browsers — Chrome, Edge, Brave, Vivaldi and Opera — and "
            + "the desktop applications that embed the same engine: chat clients, editors and "
            + "other Electron apps, the applications that show web content through Microsoft's "
            + "WebView2, and the Battle.net launcher",
        Publisher = "each application's own vendor; the storage format belongs to the Chromium "
            + "project",
        Purpose = "A site can install a service worker, a script the browser keeps for it, "
            + "which stores pages, scripts and data in this storage so the site loads quickly and "
            + "works without a connection. Web applications installed from the browser rely on it "
            + "to work offline. Chromium removes it by itself only when the disk runs short.",
        Recommendation = "Deguffer removes only the stored responses, and every service worker "
            + "stays registered to fetch them again. Most of what is stored is a copy of what the "
            + "site still serves, but a web application you use offline will not work offline until "
            + "you next open it online, and anything a site kept only here is lost.",
    };

    protected override IReadOnlyList<CacheLevel> CacheLevels => Levels;

    protected override string NothingFound =>
        "No application on this machine keeps a service worker's offline storage in its data folder.";

    protected override string WhatIsRemoved => "the offline storage";

    protected override string ContainerSentence =>
        "That storage sits inside 'Service Worker', and the directory stays: the service workers stay "
        + "registered, and only what they stored is removed.";
}
