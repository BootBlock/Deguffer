using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Thunderbird's own caches. Thunderbird is built on Firefox's engine and keeps its profiles the
/// same way, under <c>%APPDATA%\Thunderbird</c> and <c>%LOCALAPPDATA%\Thunderbird</c> rather than
/// below a <c>Mozilla</c> folder, so how a profile is found and what is asserted to survive around
/// it are <see cref="MozillaProfileCacheProvider"/>'s.
///
/// <para><b>Measured on Thunderbird 159.</b> A new profile's local half held <c>cache2</c>,
/// <c>startupCache</c> and <c>safebrowsing</c>, and nothing of Firefox's <c>thumbnails</c>,
/// <c>jumpListCache</c> or <c>remote-settings</c>, so those three are not recognised here: a
/// directory by one of their names in a Thunderbird profile is Tier 4 like any other. The cache sat
/// in the local half, so <c>browser.cache.disk.parent_directory</c> does not move it by
/// default.</para>
///
/// <para><b>The roaming half is the user's mail.</b> <c>Mail</c> and <c>ImapMail</c> hold the
/// messages, <c>abook.sqlite</c> and <c>history.sqlite</c> the address books, and
/// <c>prefs.js</c> every account. The search index <c>global-messages-db.sqlite</c> is there too
/// and is not offered: Thunderbird rebuilds it, but re-indexing every message can take a long
/// time.</para>
/// </summary>
public sealed class ThunderbirdCacheProvider : MozillaProfileCacheProvider
{
    /// <summary>
    /// What may be removed from a profile's <em>local</em> half: the three directories a measured
    /// profile held there, each of them content Thunderbird refills without being asked.
    /// </summary>
    public static readonly DisposableChildSet Children = new(
    [
        new ChildClassification(
            "cache2",
            SafetyTier.RegenerableCache,
            "Content Thunderbird downloaded and kept so it would not fetch the same thing twice. It is downloaded again when it is next wanted."),
        new ChildClassification(
            "startupCache",
            SafetyTier.RegenerableCache,
            "Precompiled interface and script data Thunderbird builds for itself. It rebuilds it on the next start, so one start is slower."),
        new ChildClassification(
            "safebrowsing",
            SafetyTier.RegenerableCache,
            "Where Thunderbird's web engine keeps its Safe Browsing lists. Thunderbird creates it again, and downloads the lists again if it uses them."),
    ]);

    /// <summary>
    /// Thunderbird's profiles, with the user data in the roaming half named for §5.6: the measured
    /// profile's files, and the mail folders and OpenPGP keyrings Mozilla documents in a profile
    /// that has accounts and keys.
    /// </summary>
    private static readonly MozillaApplication Application = new(
        "Thunderbird",
        "Thunderbird",
        Children,
        [
            new("Mail", "Your local folders, and the mail Thunderbird downloaded from POP accounts."),
            new("ImapMail", "The copies of your IMAP mail Thunderbird keeps, and each folder's settings."),
            new("News", "The newsgroups you subscribe to and the messages kept from them."),
            new("calendar-data", "Your local calendars and the copies of the others."),
            new("abook.sqlite", "Your personal address book."),
            new("history.sqlite", "The addresses Thunderbird collected from the mail you sent."),
            new("global-messages-db.sqlite", "The search index of your mail. Rebuilding it can take a long time."),
            new("key4.db", "The key that decrypts your saved passwords."),
            new("logins.json", "Your saved account passwords."),
            new("logins.db", "Your saved account passwords, as recent versions of Thunderbird keep them."),
            new("cert9.db", "The certificates and exceptions you have accepted."),
            new("secring.gpg", "Your OpenPGP secret keys."),
            new("pubring.gpg", "The OpenPGP public keys you have collected."),
            new("openpgp.sqlite", "Which OpenPGP keys you have accepted, and for whom."),
            new("prefs.js", "Every setting you have changed in Thunderbird, your accounts among them."),
        ],
        "mail, address books, saved passwords and settings",
        Kept: null);

    public ThunderbirdCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(Application, environment, runner, inspector, scanner)
    {
    }

    public override string Id => "thunderbird";

    public override string Name => "Thunderbird caches";

    public override string WhatHappensOnNextUse =>
        "Thunderbird fetches web content from the network instead of from disk for a while, and " +
        "rebuilds its startup cache the first time it opens, so one start is slower. Mail, address " +
        "books, calendars, saved passwords and settings are untouched.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.Browsers,
        Application = "Mozilla Thunderbird",
        Publisher = "Mozilla",
        Purpose = "Thunderbird is built on Firefox's engine and keeps a profile in two places, as "
            + "Firefox does. Your mail, address books, saved passwords and settings live under your "
            + "roaming profile. The disk cache, the startup cache and the Safe Browsing lists live "
            + "separately under your local profile, and Thunderbird refills every one of them by "
            + "itself.",
        Recommendation = "Deguffer removes three named cache directories from the local half of each "
            + "profile, and never removes anything at all from the half your mail is in.",
    };

    /// <summary>§5.3. Thunderbird holds its own cache open while it runs.</summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["thunderbird"];
}
