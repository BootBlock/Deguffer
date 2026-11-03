using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Firefox's own caches, which nothing in <see cref="ChromiumCacheProvider"/> reaches (1.9 GB in
/// the local half of one profile on the measured machine, 350 MB of it in the five directories
/// offered here). How a profile is found and what is asserted to survive around it are
/// <see cref="MozillaProfileCacheProvider"/>'s.
///
/// <para>The roaming half under <c>%APPDATA%\Mozilla\Firefox\Profiles\&lt;profile&gt;</c> holds
/// <c>places.sqlite</c>, <c>key4.db</c>, <c>logins.json</c>, <c>cert9.db</c> and <c>prefs.js</c> —
/// bookmarks, history, saved passwords and every preference. Firefox clears its cache only from
/// inside the running browser.</para>
/// </summary>
public sealed class FirefoxCacheProvider : MozillaProfileCacheProvider
{
    /// <summary>
    /// The synchronised dataset that dominates the local profile and is deliberately not offered.
    /// See <see cref="Children"/> for the reasoning.
    /// </summary>
    private const string SynchronisedDataName = "remote-settings";

    /// <summary>
    /// What may be removed from a profile's <em>local</em> half.
    ///
    /// <para>Every one of the five is content Firefox refills without being asked, and every one of
    /// them sits in the half of the profile that holds no user data. The names that make the
    /// allow-list matter are in the <em>other</em> half and are never enumerated at all, so they are
    /// asserted by name instead — see <see cref="Application"/>.</para>
    ///
    /// <para><c>remote-settings</c> is declared at Tier 4 rather than omitted, on the reasoning
    /// Chromium's <c>Cache</c> and <c>Service Worker</c> are: leaving it out would classify it with
    /// the generic "not recognised" sentence, when in fact it is recognised, it is four fifths of
    /// the directory, and there is a specific reason it is not on offer. Firefox synchronises it for
    /// itself — most of it the Firefox Suggest dataset — so it is re-downloadable rather than
    /// user-authored, but Mozilla documents no way to remove it and what a re-sync costs was never
    /// established. It is measured and reported instead.</para>
    /// </summary>
    public static readonly DisposableChildSet Children = new(
    [
        new ChildClassification(
            "cache2",
            SafetyTier.RegenerableCache,
            "Web content Firefox saved so it would not fetch the same thing twice. It is downloaded again when it is next wanted."),
        new ChildClassification(
            "startupCache",
            SafetyTier.RegenerableCache,
            "Precompiled interface and script data Firefox builds for itself. It rebuilds it on the next start, so one start is slower."),
        new ChildClassification(
            "safebrowsing",
            SafetyTier.RegenerableCache,
            "The downloaded Safe Browsing lists Firefox checks sites against. Firefox fetches them again shortly after it next starts."),
        new ChildClassification(
            "thumbnails",
            SafetyTier.RegenerableCache,
            "Page images for the new-tab page. Firefox draws each one again the next time you visit the page."),
        new ChildClassification(
            "jumpListCache",
            SafetyTier.RegenerableCache,
            "Icons for Firefox's taskbar jump list. Firefox rebuilds them on demand."),
        new ChildClassification(
            SynchronisedDataName,
            SafetyTier.DoNotTouch,
            "Datasets Firefox synchronises for itself, most of it the Firefox Suggest data. It is not "
            + "a cache Mozilla documents removing, and what re-downloading it would cost is not "
            + "established, so Deguffer measures it and leaves it alone."),
    ]);

    /// <summary>
    /// Firefox's profiles, with the user data in the roaming half named for §5.6. These five are
    /// the entire reason the roaming half is out of bounds.
    /// </summary>
    private static readonly MozillaApplication Application = new(
        "Firefox",
        @"Mozilla\Firefox",
        Children,
        [
            new("places.sqlite", "Your bookmarks and browsing history."),
            new("key4.db", "The key that decrypts your saved passwords."),
            new("logins.json", "Your saved usernames and passwords."),
            new("cert9.db", "The certificates and exceptions you have accepted."),
            new("prefs.js", "Every setting you have changed in Firefox."),
        ],
        "bookmarks, history, saved passwords and settings",
        new MozillaKeptData(
            SynchronisedDataName,
            "synchronised data",
            "most of it the Firefox Suggest dataset. It is left alone: Mozilla documents no way to "
            + "remove it, and what re-downloading it would cost has not been established."));

    public FirefoxCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(Application, environment, runner, inspector, scanner)
    {
    }

    public override string Id => "firefox";

    public override string Name => "Firefox caches";

    public override string WhatHappensOnNextUse =>
        "Firefox fetches pages from the network instead of from disk for a while, and rebuilds its " +
        "startup cache the first time it opens, so one start is slower. Bookmarks, history, saved " +
        "passwords, open tabs and settings are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.Browsers,
        Application = "Mozilla Firefox",
        Publisher = "Mozilla",
        Purpose = "Firefox keeps a profile in two places. Your bookmarks, history, saved passwords "
            + "and settings live under your roaming profile. The disk cache, the startup cache and "
            + "the downloaded Safe Browsing lists live separately under your local profile, and "
            + "Firefox refills every one of them by itself.",
        Recommendation = "Deguffer removes five named cache directories from the local half of each "
            + "profile, and never removes anything at all from the half your bookmarks and "
            + "passwords are in.",
    };

    /// <summary>§5.3. Firefox holds its own cache open while it runs.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["firefox"];
}
