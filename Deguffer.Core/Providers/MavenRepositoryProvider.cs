using System.Xml;
using System.Xml.Linq;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// Maven's local repository, the directory every dependency a build resolves is copied into.
/// Researched rather than measured: no Maven installation was present on the machine this was
/// written against.
///
/// <para><b>Tier 2, not the Tier 1 the survey proposed, and the reason is a directory Maven fills
/// from two different places.</b> Most of what is in there was downloaded from a remote repository
/// and comes back on the next build with nothing but time and bandwidth spent — Tier 1's own
/// description. But <c>mvn install</c> writes into the same tree, under the same layout, and what
/// it writes was built on this machine and exists on no remote at all. Losing one of those does not
/// make the next build slower: it makes it fail, with an unresolvable dependency, until somebody
/// rebuilds the project that produced it. That is the shape Playwright is already Tier 2 for — a
/// broken build followed by a deliberate manual step, rather than a slower one.</para>
///
/// <para><b>The two halves cannot be told apart from the filesystem with any confidence, so the
/// whole is offered at the more cautious tier.</b> A downloaded artefact usually carries a
/// <c>_remote.repositories</c> marker naming the repository it came from and a locally installed
/// one usually does not, but that file is a Maven implementation detail rather than a contract, it
/// is absent from older trees entirely, and a rule that deleted a version directory on the strength
/// of it would be guessing about the one case that cannot be undone. §5.2's direction for an
/// uncertain classification is to leave it alone, and the honest form of that here is one row the
/// user acknowledges rather than a split nobody can verify.</para>
///
/// <para><b>§5.1 was asked and there is no answer.</b> Maven ships no machine-wide purge.
/// <c>dependency:purge-local-repository</c> is a per-project goal that removes what one project
/// resolves and then resolves it again, which is neither the scope nor the effect a disk cleaner
/// wants, and running it needs a project directory Deguffer does not have. So this is path-based by
/// necessity.</para>
///
/// <para><b>§5.2's trap is a file, and the survey names it.</b> <c>settings.xml</c> sits in the same
/// root and holds server credentials, which may be encrypted against the master password in
/// <c>settings-security.xml</c> beside it. Neither is ever enumerated, because this provider names
/// the one directory it removes rather than listing the root that contains it — the choice
/// <see cref="DeclaredRoot"/> exists for. An enumeration would have nothing to add here: everything
/// in <c>.m2</c> other than the repository is either configuration or the small wrapper
/// distribution, all of it knowable by name, so listing the root would buy no reporting a
/// declaration does not already give.</para>
/// </summary>
public sealed class MavenRepositoryProvider : CleanupProviderBase
{
    /// <summary>
    /// The one property this reads from a configured path. Maven interpolates its whole property
    /// set into <c>localRepository</c>, which Deguffer cannot reproduce, but this one is the common
    /// idiom for writing a portable settings file and resolving it is exact rather than a guess.
    /// A value naming any other property is left unresolved and the provider says so.
    /// </summary>
    private const string UserHomeProperty = "${user.home}";

    /// <summary>
    /// Things in the Maven home that §5.6 must assert survived. Named rather than enumerated, and
    /// the first two are the whole reason the root is never a target.
    /// </summary>
    private static readonly (string RelativePath, string Reason)[] ProtectedNames =
    [
        ("settings.xml", "User Maven configuration, which holds server credentials and private repository URLs."),
        ("settings-security.xml", "The master password Maven decrypts stored server passwords with."),
        ("toolchains.xml", "The JDK toolchains this user's builds are configured to use."),
        ("wrapper", "Maven distributions the wrapper downloaded. Small, and not what this provider removes."),
    ];

    private string? _localRepository;

    public MavenRepositoryProvider(
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

    public override string Id => "maven";

    public override string Name => "Maven local repository";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next Maven build downloads every dependency it needs again, which for a large project "
        + "is gigabytes over the network. Anything you installed locally with 'mvn install' was "
        + "never on a remote, so a build depending on one of those fails until you rebuild the "
        + "project that produced it. Your settings, stored server passwords and toolchains are untouched.";

    protected override IReadOnlyList<string> ConflictingProcessNames => ["java", "mvn"];

    /// <summary>The Maven home. Never a target, and what §5.6 asserts survived.</summary>
    public string Home => Path.Combine(Environment.UserProfile, ".m2");

    /// <summary>Where Maven keeps the local repository when nothing has moved it.</summary>
    public string DefaultLocalRepository => Path.Combine(Home, "repository");

    /// <summary>
    /// The local repository, honouring <c>localRepository</c> in the user's <c>settings.xml</c>.
    /// Null when that element holds something this cannot resolve to a full path, because
    /// targeting a directory nobody pointed at is exactly the guess §5.2 forbids.
    ///
    /// <para>Two ways of moving it are deliberately out of reach, and both fail safe. Maven merges
    /// a global <c>settings.xml</c> from its own installation directory, which the user's file
    /// overrides and which Deguffer would have to locate an installation to read; and
    /// <c>-Dmaven.repo.local</c> is chosen per invocation and exists nowhere on disk to be read at
    /// all. Where either is in play this measures and offers the directory Maven's user settings
    /// name, which is the one the user configured, so the failure is a smaller reclaim rather than
    /// a wrong target.</para>
    /// </summary>
    public string? ResolveLocalRepository()
    {
        if (_localRepository is not null)
        {
            return _localRepository;
        }

        if (ReadConfiguredRepository() is not { } configured)
        {
            return _localRepository = DefaultLocalRepository;
        }

        // Normalised rather than used as it arrived. A trailing separator would make the leaf name
        // empty, and a location with an empty relative path resolves back to the root that holds it
        // — so the plan would target the very directory it also asserts must survive, and §5.6 would
        // report a correct run as a failure. A value ending in '..' is worse: LongPath.Extended
        // requires an already-resolved path, so the removal would land a directory higher than the
        // plan named.
        return _localRepository = LongPath.Configured(configured);
    }

    /// <summary>
    /// <c>settings.xml</c> is edited by hand and by every IDE, so the repository can move between
    /// one scan and the next. A remembered answer would measure a directory Maven has stopped
    /// filling.
    /// </summary>
    public override void InvalidateCaches()
    {
        _localRepository = null;
        base.InvalidateCaches();
    }

    /// <summary>
    /// Presence is the repository being on disk rather than <c>mvn</c> being on <c>PATH</c>: an IDE
    /// ships its own Maven and fills this directory without ever putting the command anywhere
    /// Deguffer would find it.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(ResolveLocalRepository() is { } repository && LongPath.DirectoryExists(repository));

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        if (ResolveLocalRepository() is not { } repository)
        {
            return EmptyPlan(
                "The localRepository in your Maven settings.xml is not a full path, so Deguffer cannot "
                + "tell which directory it means and is leaving it alone.");
        }

        // §5.2, and the reason this check is here rather than trusted to the declaration. A
        // configured value naming the Maven home, or anything above it, would make the tool root the
        // target: the same plan would delete .m2 while asserting that the settings.xml inside it
        // survives. '${user.home}/.m2' is a plausible typo for the correct
        // '${user.home}/.m2/repository', and a settings file arrives from a dotfiles repository as
        // often as it is typed, so this is refused rather than trusted.
        if (Contains(repository, Home))
        {
            return EmptyPlan(
                $"Your Maven settings.xml points the local repository at {repository}, which holds "
                + "your Maven configuration rather than sitting inside it. Deguffer is leaving it alone.");
        }

        if (Declare(repository) is not { } roots)
        {
            return EmptyPlan(
                $"Your Maven settings.xml points the local repository at {repository}, which is a whole "
                + "volume rather than a directory inside one. Deguffer does not target a volume root.");
        }

        var scan = DeclaredLocations.Examine(roots, ct);

        if (scan.FoundNothing)
        {
            return EmptyPlan($"Maven has not downloaded anything on this machine ({repository} is absent).");
        }

        var notes = new List<PlanNote>(scan.Notes);

        var (steps, measured) = await PlanDeletionsAsync(scan.Targets, ct).ConfigureAwait(false);

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
            ProtectedPaths = Protect([.. scan.Protected]),
            Notes = notes,
            Fallback = measured.Fallback,
        };
    }

    /// <summary>
    /// The repository as a declared path under the directory that holds it, and the Maven home as a
    /// root with nothing to remove in it.
    ///
    /// Two roots rather than one, because a relocated repository does not live under <c>.m2</c> and
    /// the credentials that must survive still do. The second root declares no location at all,
    /// which is how §5.6 gets its subjects on a machine where the two are far apart. Null when the
    /// configured repository is a volume root and so has no containing directory.
    /// </summary>
    private IReadOnlyList<DeclaredRoot>? Declare(string repository)
    {
        if (Path.GetDirectoryName(repository) is not { Length: > 0 } container)
        {
            return null;
        }

        var location = new DeclaredLocation(
            Path.GetFileName(repository),
            "Every dependency Maven has resolved for a build on this machine. The downloaded ones are "
            + "fetched again on the next build; anything installed here by 'mvn install' has to be rebuilt.",
            DeclaredLocationKind.Directory,

            // A repository nests by group, artifact and version before it reaches a file, so its top
            // level moves only when a whole new group first appears — see DeclaredLocation.ReportsAge.
            ReportsAge: false);

        var inHome = container.Equals(Home, StringComparison.OrdinalIgnoreCase);

        var repositoryRoot = new DeclaredRoot(
            container,
            inHome
                ? "The .m2 root itself must survive — only the repository inside it is removed."
                : "The directory holding the local repository must survive — only the repository inside it is removed.",
            RequiresElevation: false,
            [location],
            inHome ? ProtectedNames : []);

        if (inHome)
        {
            return [repositoryRoot];
        }

        return
        [
            repositoryRoot,
            new DeclaredRoot(
                Home,
                "The .m2 root itself must survive — nothing inside it is removed once the repository has moved elsewhere.",
                RequiresElevation: false,
                [],
                ProtectedNames),
        ];
    }

    /// <summary>Whether <paramref name="candidate"/> is <paramref name="ancestor"/> or sits inside it.</summary>
    private static bool Contains(string ancestor, string candidate) =>
        candidate.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(
            Path.TrimEndingDirectorySeparator(ancestor) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>localRepository</c> element of the user's settings file, or null if there is not one.
    ///
    /// Matched on the local name, because Maven's own schema puts the file in a namespace and a
    /// great many real settings files omit it. An unreadable or malformed file is treated as no
    /// override rather than as an error: Maven itself would refuse to build, which the user will
    /// hear about from Maven, and the default location is the right thing for Deguffer to fall back
    /// on either way.
    /// </summary>
    private string? ReadConfiguredRepository()
    {
        var settings = Path.Combine(Home, "settings.xml");

        if (!LongPath.FileExists(settings))
        {
            return null;
        }

        try
        {
            // Opened as a stream rather than by path, because XDocument.Load treats a string as a
            // URI and §6.3's extended-length prefix is not one — it throws before it reads a byte.
            using var stream = new FileStream(
                LongPath.Extended(settings), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var configured = XDocument
                .Load(stream)
                .Root?
                .Elements()
                .FirstOrDefault(e => e.Name.LocalName == "localRepository")?
                .Value
                .Trim();

            if (string.IsNullOrEmpty(configured))
            {
                return null;
            }

            return configured.StartsWith(UserHomeProperty, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Environment.UserProfile, configured[UserHomeProperty.Length..].TrimStart('/', '\\'))
                : configured;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
