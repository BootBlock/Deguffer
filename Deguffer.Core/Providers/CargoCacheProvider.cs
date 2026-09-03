using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Cargo's downloaded crate archives, the sources unpacked from them, and the working copies of its
/// git dependencies. Reported to reach 50 GB on a working Rust machine; researched rather than
/// measured, because no Rust toolchain was installed on the machine this was written against.
///
/// <para><b>§5.1 was asked and the answer is no.</b> Cargo's garbage collection is still unstable —
/// it is reached through <c>-Z gc</c> on a nightly toolchain — so there is no command a released
/// Cargo will honour. <c>cargo clean</c> is a different subject entirely: it empties one project's
/// <c>target</c> directory and never touches the shared home. So this is the §5.2 path-based case,
/// on Gradle's shape.</para>
///
/// <para><b>The §5.2 trap is live rather than theoretical.</b> <c>credentials.toml</c> holds the
/// registry authentication tokens <c>cargo login</c> wrote, <c>config.toml</c> holds the user's
/// Cargo configuration, and <c>bin</c> holds every binary installed with <c>cargo install</c> or
/// rustup — normally on <c>PATH</c>. All three sit in the same root as the caches, so the root is
/// never a target and the two configuration files are named survivors: a child classification only
/// ever sees directories, so a file in the root is invisible to it.</para>
///
/// <para><b>Two children the survey listed as disposable are deliberately not targeted, for the
/// same reason in both cases: they are the originals, and what is targeted is derived from
/// them.</b> Cargo's own documentation draws that line when it says which parts of the home are
/// worth carrying between CI runs — <c>registry\index</c>, <c>registry\cache</c> and <c>git\db</c>
/// are, and <c>registry\src</c> and <c>git\checkouts</c> are not, because those two are re-derived
/// locally from the first three. <c>git\db</c> is the sharper of the pair: it is the bare clone of
/// a git dependency, it is the only copy of that history on this machine, and it can be fetched
/// again only while the remote repository still exists, is still reachable and still carries the
/// revision a lock file names. Tier 1 requires that whatever produced the content re-creates it on
/// demand, and for a git remote that is a claim about somebody else's server. So the safe half and
/// the unsafe half are split rather than shipped together, and the split runs along a directory
/// boundary the provider can draw with certainty.</para>
///
/// <para><c>%USERPROFILE%\.rustup\toolchains</c> holds a full toolchain per installed
/// channel. Nothing reaches it today, and when something does it belongs at Tier 2 in a provider of
/// its own rather than as a child of this one.</para>
/// </summary>
public sealed class CargoCacheProvider : CleanupProviderBase
{
    /// <summary>Set by the user to move the whole Cargo home, caches and configuration together.</summary>
    public const string HomeVariable = "CARGO_HOME";

    /// <summary>
    /// What may be deleted, one containing directory at a time. Anything not named here is Tier 4
    /// by construction, which is the direction §5.2 requires the unknown case to fail in.
    ///
    /// <para>Three levels rather than one, because the caches sit below <c>registry</c> and
    /// <c>git</c> rather than in the root — see <see cref="CacheLevel"/> for why that is expressed
    /// as a level per directory instead of as a deeper allow-list. The root is enumerated as well,
    /// and that is the deliberate half of the choice: <c>.cargo</c> is an ordinary directory in the
    /// user's own profile, listing it is not itself a hazard, and a child Cargo adds in a later
    /// release is then reported as left alone rather than being invisible inside a folder the user
    /// can see is larger than the total.</para>
    /// </summary>
    public static readonly IReadOnlyList<CacheLevel> Levels =
    [
        new CacheLevel(string.Empty, new DisposableChildSet(
        [
            new ChildClassification(
                "registry",
                SafetyTier.DoNotTouch,
                "Cargo's registry directory. It stays: only the downloaded archives and the sources unpacked from "
                + "them are removed, and the index beside those is left alone."),
            new ChildClassification(
                "git",
                SafetyTier.DoNotTouch,
                "Cargo's git-dependency directory. It stays: only the working checkouts inside it are removed, and "
                + "the clones they are made from are left alone."),
            new ChildClassification(
                "bin",
                SafetyTier.DoNotTouch,
                "Executables installed with 'cargo install' or rustup. This directory is normally on PATH, and "
                + "nothing re-creates what is in it."),
        ])),
        new CacheLevel("registry", new DisposableChildSet(
        [
            new ChildClassification(
                "cache",
                SafetyTier.RegenerableCache,
                "Downloaded .crate archives. Cargo downloads each one again from the registry the next time a build asks for it."),
            new ChildClassification(
                "src",
                SafetyTier.RegenerableCache,
                "Crate sources unpacked from the archives beside them. Cargo unpacks them again, with no download while those archives are there."),
            new ChildClassification(
                "index",
                SafetyTier.DoNotTouch,
                "Registry metadata for every published crate. Cargo can fetch it again, but it is what lets a build "
                + "resolve versions offline and it is small beside the archives, so Deguffer leaves it."),
        ])),
        new CacheLevel("git", new DisposableChildSet(
        [
            new ChildClassification(
                "checkouts",
                SafetyTier.RegenerableCache,
                "Working copies of git dependencies, checked out from the clones in 'db'. Cargo re-creates each one from that clone without a network."),
            new ChildClassification(
                "db",
                SafetyTier.DoNotTouch,
                "The bare clone of each git dependency, and the only copy of that history on this machine. The "
                + "checkouts are re-created from it offline, and it can be fetched again only while the remote "
                + "repository still exists."),
        ])),
    ];

    private const string LinkReason =
        "A link rather than a directory, so what it points at was never classified.";

    /// <summary>
    /// Files in the Cargo home that §5.6 must assert survived. Named separately because a
    /// <see cref="DisposableChildSet"/> only ever classifies a directory, so a file in the root is
    /// never enumerated, never classified and never asserted unless it is named here.
    ///
    /// Both spellings of the two configuration files are listed: Cargo still reads the
    /// extensionless <c>config</c> and <c>credentials</c> that predate the <c>.toml</c> suffix, and
    /// whichever is absent records itself as nothing to preserve rather than as a pass.
    /// </summary>
    private static readonly (string Name, string Reason)[] ProtectedFiles =
    [
        ("credentials.toml", "Registry authentication tokens written by 'cargo login'."),
        ("credentials", "Registry authentication tokens written by 'cargo login', under the older filename."),
        ("config.toml", "User Cargo configuration, which may name private registries and their credential helpers."),
        ("config", "User Cargo configuration, under the older filename."),
        (".crates.toml", "Cargo's record of what 'cargo install' put in bin."),
        (".crates2.json", "Cargo's record of what 'cargo install' put in bin."),
    ];

    public CargoCacheProvider(
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
    }

    public override string Id => "cargo";

    public override string Name => "Cargo crate cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The next cargo build downloads the crate archives it needs again and unpacks them, so it "
        + "spends longer fetching before it compiles. Registry metadata, the clones of git "
        + "dependencies, your configuration and anything installed with 'cargo install' are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Cargo, the build tool and package manager for Rust",
        Publisher = "the Rust project",
        Purpose = "Every Rust project on the machine shares one Cargo home. It holds the crate "
            + "archives crates.io served, those archives unpacked for the compiler to read, the "
            + "index used to resolve versions, and clones of any dependency pulled straight from a "
            + "git repository.",
        Recommendation = "Deguffer removes the downloaded archives, the sources unpacked from "
            + "them, and the working copies of git dependencies — each of which Cargo fetches, "
            + "unpacks or checks out again on demand. The registry index and the bare git clones "
            + "stay: those clones are the only copy of that history on this machine.",
    };

    protected override IReadOnlyList<string> ConflictingProcessNames => ["cargo", "rustc", "rust-analyzer"];

    /// <summary>Where Cargo keeps its home when <see cref="HomeVariable"/> is unset.</summary>
    public string DefaultHome => Path.Combine(Environment.UserProfile, ".cargo");

    /// <summary>
    /// The Cargo home, honouring <see cref="HomeVariable"/>. Null when that variable holds
    /// something this cannot resolve: Cargo resolves a relative value against the invoking shell's
    /// working directory, which Deguffer is not, so there is no correct interpretation available and
    /// enumerating a directory nobody pointed at is exactly the guess §5.2 forbids.
    ///
    /// Normalised through <see cref="LongPath.Configured"/> rather than used as it arrived, so every
    /// path derived from it is canonical. A trailing separator would otherwise leave the home with no
    /// name of its own to put in a note, and a value spelled differently from what the enumeration
    /// returns would defeat the comparison that stops one directory being reported twice.
    /// </summary>
    public string? ResolveHome() =>
        Environment.GetEnvironmentVariable(HomeVariable) is { } configured && configured.Trim().Length > 0
            ? LongPath.Configured(configured)
            : DefaultHome;

    /// <summary>
    /// One root per level, because that is how Cargo's own declaration is written: <c>registry</c>
    /// and <c>git</c> are Tier 4 containers at the home's level and classify their own children at
    /// theirs. Declaring only the home would refuse <c>registry\cache</c>, which Deguffer removes.
    ///
    /// Empty where <see cref="HomeVariable"/> names something that is not a full path. There is no
    /// directory to make a claim about, and guessing one is the §5.2 failure this whole declaration
    /// exists to prevent.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        ResolveHome() is { } home
            ?
            [
                .. Levels.Select(level => ToolRoot.Of(
                    level.Resolve(home),
                    "This is inside Cargo's own folder. Deguffer removes the downloaded archives, "
                    + "the sources unpacked from them and the git checkouts, and nothing else — the "
                    + "registry index, the bare clones and the credentials beside them all stay.",
                    level.Children)),
            ]
            : [];

    /// <summary>
    /// Presence is a cache actually on disk, never the home existing. Installing rustup creates
    /// <c>.cargo</c> with nothing in it but <c>bin</c>, and reporting that as a source would offer
    /// the user a row the plan then has nothing to say about.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveHome() is { } home && RecognisedCachePaths(home).Any(LongPath.DirectoryExists));

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        if (ResolveHome() is not { } home)
        {
            return EmptyPlan(
                $"{HomeVariable} is set to '{Environment.GetEnvironmentVariable(HomeVariable)?.Trim()}', which is not "
                + "a full path. Deguffer cannot tell which directory that means, so it is leaving it alone.");
        }

        if (!LongPath.DirectoryExists(home))
        {
            return EmptyPlan($"Cargo is not installed for this user — no {home} directory.");
        }

        // The home arrives by name, from an environment variable or a default, so nothing has
        // classified it. The level walk below cannot catch a junctioned home on its own: it declines
        // the level it is looking at and returns, and the next level then resolves its own path
        // through the very link that was declined, finding ordinary directories on the far side.
        // Those would be targeted while every survivor named for this home resolves through the same
        // link and passes — §5.6's negative made vacuous, in the worst of its forms.
        if (LongPath.IsReparsePoint(home))
        {
            return EmptyPlan(
                $"Leaving '{home}' alone: it is a link to somewhere else, and Deguffer does not look "
                + "through a link.");
        }

        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();

        // A container that is a link is met twice: once as a link child of the level above it, and
        // once as a level whose own directory turns out to be one. Both times it is the same path,
        // so without this the plan carries the sentence twice and §5.6 reports one survivor as two.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var unreadable = false;

        foreach (var level in Levels)
        {
            ct.ThrowIfCancellationRequested();
            unreadable |= !CollectFrom(level, home, targets, declined, seen, notes, ct);
        }

        // The home was found on disk by name above, and a full path resolves through a directory the
        // account may not list. So "Cargo has downloaded nothing" would deny what this same method
        // established a few lines earlier.
        if (targets.Count == 0 && declined.Count == 0 && !unreadable)
        {
            return EmptyPlan($"Cargo has downloaded nothing into {home} yet.");
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        if (BuildRunningProcessNote() is { } warning)
        {
            notes.Add(warning);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect([.. BuildProtectedPaths(home), .. declined]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
        };
    }

    /// <summary>
    /// §5.2 for one level: classify the children of one directory, target the recognised ones, and
    /// say plainly what is being left behind. A spared child is asserted to survive as well as
    /// omitted, because it is a sibling of a targeted one under the same parent — which is exactly
    /// when an over-broad rule takes both.
    /// </summary>
    /// <returns>
    /// False where a level's directory would not be listed, so the caller can keep the plan from
    /// claiming Cargo has downloaded nothing.
    /// </returns>
    private static bool CollectFrom(
        CacheLevel level,
        string home,
        List<DeletionTarget> targets,
        List<(string Path, string Reason)> declined,
        HashSet<string> seen,
        List<PlanNote> notes,
        CancellationToken ct)
    {
        var directory = level.Resolve(home);

        if (!LongPath.DirectoryExists(directory))
        {
            return true;
        }

        // Applied at every level rather than only at the root, which is the one level reached by
        // name. A junctioned 'registry' hands back the far side's ordinary directories, and a
        // recognised name among them would be targeted while every survivor named for this home
        // resolves through the link and passes — §5.6's negative made vacuous.
        if (LongPath.IsReparsePoint(directory))
        {
            Decline(directory, OwnName(level, directory), LinkReason);
            return true;
        }

        var scan = ChildDirectories.Under(directory);

        if (scan.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(directory));
            return false;
        }

        foreach (var link in scan.Links)
        {
            Decline(LongPath.Display(link.FullName), Qualify(level, link.Name), LinkReason);
        }

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var classification = level.Children.Classify(child.Name);
            var path = LongPath.Display(child.FullName);

            if (!classification.Tier.IsOfferable())
            {
                Decline(path, Qualify(level, child.Name), classification.Reason);
                continue;
            }

            targets.Add(new DeletionTarget(path, classification.Reason));
        }

        return true;

        void Decline(string path, string label, string reason)
        {
            if (!seen.Add(path))
            {
                return;
            }

            notes.Add(new PlanNote(PlanNoteSeverity.Information, $"Leaving '{label}' alone: {reason}"));
            declined.Add((path, reason));
        }
    }

    /// <summary>
    /// A child's name, qualified by the directory it was classified in. Two levels here hold a child
    /// whose name means nothing on its own — 'cache' under registry is not the same subject as a
    /// 'cache' elsewhere — and an unqualified note would leave the user unable to place it.
    /// </summary>
    private static string Qualify(CacheLevel level, string name) =>
        level.ContainerName.Length == 0 ? name : Path.Combine(level.ContainerName, name);

    /// <summary>
    /// What to call a level's own directory. Never qualified, because a level is not inside itself:
    /// combining would name a path that does not exist.
    /// </summary>
    private static string OwnName(CacheLevel level, string directory) =>
        level.ContainerName.Length == 0 ? Path.GetFileName(directory) : level.ContainerName;

    /// <summary>
    /// §5.6. The home itself and the configuration in it are the whole reason this provider
    /// classifies children rather than removing a directory, so they are what a run has to prove.
    /// </summary>
    private static IEnumerable<(string Path, string Reason)> BuildProtectedPaths(string home)
    {
        yield return (home, "The Cargo home itself must survive — only its known-disposable children are removed.");

        foreach (var (name, reason) in ProtectedFiles)
        {
            yield return (Path.Combine(home, name), reason);
        }
    }

    /// <summary>
    /// Every path this provider could ever target, by declaration rather than by enumeration — so
    /// answering "is there anything here?" costs one existence check each and can never reach a
    /// child the table does not name.
    /// </summary>
    private static IEnumerable<string> RecognisedCachePaths(string home) =>
        from level in Levels
        from name in level.Children.DisposableNames
        select Path.Combine(level.Resolve(home), name);
}
