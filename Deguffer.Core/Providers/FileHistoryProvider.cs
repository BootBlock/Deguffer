using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The saved versions Windows File History keeps on whichever drive the user pointed it at.
///
/// <para><b>It grows without bound by design.</b> <c>FH_RETENTION_TYPES</c> defaults to
/// <c>FH_RETENTION_DISABLED</c> — "previous versions are never deleted from the backup target" — so
/// a File History drive fills up because that is what it was configured to do, not because anything
/// went wrong. That is why these targets routinely reach tens of gigabytes.</para>
///
/// <para><b>Tier 3, and it is not a borderline call.</b> Tier 2 is "regenerable at a cost". A
/// superseded version of a file is not regenerable at any cost: the state it captures exists
/// nowhere else, and nothing can recreate the way a document was in March. So this is offered,
/// never pre-selected, and never carried out without a confirmation that says the loss is
/// permanent.</para>
///
/// <para><b>§5.1: the command, and never the directory.</b> Windows ships
/// <c>FhManagew.exe -cleanup &lt;days&gt;</c>, documented to delete a version only when <em>both</em>
/// of two conditions hold: it is older than the given age, <em>and</em> either the file has left the
/// protection scope or a newer version of it is on the target. That second condition is what
/// guarantees the last copy of a protected file survives, and <b>it is a property of the command
/// that no path deletion could reproduce</b>. §5.2 forbids the directory independently: the target's
/// layout is documented nowhere by Microsoft, so every child of it is unrecognised and therefore
/// Tier 4. Deguffer names those folders only to size one of them and to assert afterwards that the
/// rest are still there.</para>
///
/// <para><b><c>-cleanup 0</c> is never offered.</b> It keeps only the newest version of files
/// <em>currently in the protection scope</em>, which silently discards every version of everything
/// the user has since moved, renamed or deleted. The retention age is clamped to at least
/// <see cref="MinimumRetentionDays"/> here as well as in the settings box, because nothing validates
/// a hand-edited <c>preferences.json</c> on the way in.</para>
///
/// <para><b>The preview is an upper bound, and it says so.</b> <c>FhManagew.exe</c> reports nothing
/// in advance, so the figure is Deguffer's own measurement of the first of Microsoft's two
/// conditions: what the target holds that is older than the retention age. The second condition can
/// only reduce it, so the number is a ceiling rather than a forecast — it is carried as
/// <see cref="ScanSize.Approximate"/>, rendered as "about", and the plan says in plain words that
/// Windows keeps the newest copy of anything it still protects. The honest figure for what was
/// actually freed is the one measured after the run, which the executor already reports.</para>
///
/// <para><b>File History is not deprecated.</b> It appears on neither Microsoft's deprecated- nor
/// removed-features list. Its configuration <em>API</em> is, which is a different thing, and Backup
/// and Restore (Windows 7) is a different feature again.</para>
/// </summary>
public sealed class FileHistoryProvider : CleanupProviderBase
{
    /// <summary>
    /// The floor on the retention age, and a safety rule rather than input validation. See the class
    /// remarks for what <c>-cleanup 0</c> does.
    /// </summary>
    public const int MinimumRetentionDays = 1;

    /// <summary>
    /// Ten years, which is <see cref="MinimumAge.MaximumWindow"/>. Beyond it the measurement this
    /// provider makes cannot be expressed, and a retention age past the age of the machine asks
    /// Windows to remove nothing.
    /// </summary>
    public static readonly int MaximumRetentionDays = (int)MinimumAge.MaximumWindow.TotalDays;

    /// <summary>
    /// The command, resolved on <c>PATH</c> rather than assumed under <c>System32</c>. It ships with
    /// every Windows 11 install, so its presence says nothing about whether File History is set up —
    /// see <see cref="FileHistoryDiscovery"/>.
    /// </summary>
    private const string ManagerCommand = "FhManagew";

    private readonly FileHistoryDiscovery _discovery;

    /// <summary>
    /// Read at plan time rather than held, so a retention age changed on the Settings page takes
    /// effect from the next preview. See <see cref="ICurrentPreferences"/>.
    /// </summary>
    private readonly ICurrentPreferences _preferences;

    public FileHistoryProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ICurrentPreferences? preferences = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _discovery = new FileHistoryDiscovery(Environment);
        _preferences = preferences ?? DefaultPreferences.Instance;
    }

    public override string Id => "file-history";

    public override string Name => "Windows File History";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override string WhatHappensOnNextUse =>
        "Saved versions older than the retention age are gone permanently, so you can no longer go "
        + "back to how a file was on a date before it. Windows will not remove the newest copy of a "
        + "file it is still protecting, and it keeps backing up exactly as it did.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "File History, the versioned backup built into Windows",
        Publisher = "Microsoft",
        Purpose = "File History saves a copy of every file in your protected folders each time it "
            + "changes, on a drive you chose. It is set never to delete an old version unless you "
            + "tell it otherwise, so the drive keeps filling for as long as it is switched on.",
        Recommendation = "Deguffer asks Windows' own command to drop versions past an age you set, "
            + "and never deletes anything on the drive itself. A version is a snapshot of a file as "
            + "it was, so nothing can bring one back once it goes.",
    };

    /// <summary>
    /// §5.3. The backup engine writes into the same folder this trims, so a backup running during a
    /// clean is worth saying out loud. It is a warning rather than a refusal: both are Windows'
    /// own, and the command is the one Microsoft documents for the job.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => ["FileHistory"];

    /// <summary>The retention age in force, clamped to the bounds the settings box also binds.</summary>
    private int RetentionDays => Math.Clamp(
        _preferences.Current.FileHistoryRetentionDays, MinimumRetentionDays, MaximumRetentionDays);

    /// <summary>
    /// §5.2 as §7.1 needs it read from outside: the target's <c>FileHistory</c> folder holds every
    /// account's backups, and <b>nothing inside it is disposable by path</b>. The layout is
    /// undocumented, so every child is unrecognised and Tier 4 — which is the whole reason this
    /// provider runs a command instead.
    ///
    /// <para>Empty until a target has been located, which costs one configuration read and a few
    /// existence checks rather than a walk, and is held for the pass.</para>
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
        _discovery.Locate().Target is { } target
            ?
            [
                new ToolRoot(
                    // Display form, which is ToolRoot's contract. On a drive with no letter the
                    // target is named in the device namespace, and LongPath.Display leaves such a
                    // path alone rather than producing one that resolves nowhere (§6.3).
                    LongPath.Display(target.FileHistoryRoot),
                    "This is your File History backup, and it holds every saved version of your own "
                    + "files as well as anyone else's who backs up to this drive. Deguffer never "
                    + "removes anything here itself.",
                    static _ => false),
            ]
            : [];

    /// <summary>
    /// Presence is File History being set up for this account, never <c>FhManagew.exe</c> existing:
    /// the command ships with Windows whether or not the feature was ever used, so reading it as a
    /// hit would report a source on every machine and plan nothing on almost all of them.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(_discovery.IsConfigured);

    /// <summary>
    /// The target is remembered for the life of a pass, so a backup drive plugged in while the app
    /// was open needs it dropped like every other cached view of the machine.
    /// </summary>
    public override void InvalidateCaches()
    {
        _discovery.Invalidate();
        base.InvalidateCaches();
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var located = _discovery.Locate();

        if (located.Target is not { } target)
        {
            return DescribeAbsence(located.Outcome);
        }

        var manager = Environment.FindExecutable(ManagerCommand);
        if (manager is null)
        {
            return UnexaminedPlan(
                $"Windows' own File History command ({ManagerCommand}.exe) is not on this machine, "
                + "so nothing is offered — the saved versions are only ever removed by asking "
                + "Windows to remove them.");
        }

        var days = RetentionDays;

        // The whole folder for the after-run delta, and the first of Microsoft's two conditions for
        // the estimate. MinimumAge reads the newer of a file's creation and last-write times, so a
        // version copied onto the target counts from when it arrived rather than from whenever the
        // original was last edited — which is the age the command is judging, and errs towards
        // keeping a file rather than counting it.
        var (probed, aged) = await MeasureAgedAsync([target.DataDirectory], RetentionAge(days), ct)
            .ConfigureAwait(false);

        var survivors = ProtectedNeighboursOf(target, out var unreadable);

        var notes = new List<PlanNote>
        {
            new(PlanNoteSeverity.Information,
                $"Windows is saving this machine's File History to {target.Root}."),
            new(PlanNoteSeverity.Information,
                $"The figure is this machine's saved versions older than {days} days, which is as "
                + "much as Windows could remove. It keeps the newest copy of any file it is still "
                + "protecting, so it will usually free less. Nothing else on that drive is counted, "
                + "and nothing else on it is touched."),
        };

        if (probed.Note is { } scanNote)
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
            Steps =
            [
                new RunCommandStep(
                    manager,
                    $"-cleanup {days} -quiet",
                    $"Drop File History versions older than {days} days using Windows' own command")
                {
                    Estimated = ScanSize.Approximate(aged.Total.Reclaimable),
                    MeasuredPaths = [target.DataDirectory],
                    MeasuredBefore = probed.Total,

                    // The one command step in this product whose figure is not the whole of what it
                    // measures: the command takes only what is past the retention age, so a target
                    // full of recent versions measures zero and is not clear. Without this the row
                    // would claim it was. See CleanupStep.WithheldRecent.
                    WithheldRecent = aged.WithheldRecent.Any(held => held),
                },
            ],
            ProtectedPaths = Protect([.. survivors]),
            Notes = notes,
            Fallback = probed.Fallback,
            HasUnreadableRoot = unreadable,
        };
    }

    /// <summary>
    /// The cut-off the command is being asked for, as the guard the scanners already filter on. The
    /// polarity matches exactly: <c>-cleanup N</c> considers versions older than N days, and a
    /// <see cref="MinimumAge"/> of N days measures everything a deletion would be allowed to take.
    /// </summary>
    private static MinimumAge RetentionAge(int days) =>
        MinimumAge.Within(TimeSpan.FromDays(days), DateTime.UtcNow);

    /// <summary>
    /// Why there is nothing to offer. The two cases ask for different things — nothing at all, and
    /// plugging a drive in — so they are not one sentence.
    ///
    /// <para>Only two, because <see cref="FileHistoryLookup"/> makes only two claims it can support.
    /// "The drive is unplugged" and "the settings named nothing Deguffer could use" are one outcome
    /// there, so the sentence names the likely reason without asserting it.</para>
    /// </summary>
    private CleanupPlan DescribeAbsence(FileHistoryLookup outcome) => outcome switch
    {
        FileHistoryLookup.NotConfigured =>
            EmptyPlan("File History is not set up on this machine, so there are no saved versions."),
        _ => UnexaminedPlan(
            "File History is set up, and Deguffer did not find this machine's saved versions on any "
            + "drive its settings name. The drive it saves to may not be connected. Nothing is "
            + "offered rather than guessed at."),
    };

    /// <summary>
    /// §5.6, and §5.2's unrecognised case stated by name rather than by omission.
    ///
    /// <para>A File History drive is shared twice over: one folder per account under
    /// <c>FileHistory</c>, and one folder per machine under each account. Somebody else's backup and
    /// this machine's backup are siblings of identical shape, so the folders that must survive are
    /// listed individually rather than left out silently.</para>
    ///
    /// <para>The catalogue beside the versions is the other one worth naming. Removing it would
    /// leave every saved version on the drive intact and unreachable, which is a failure no size
    /// comparison would show.</para>
    ///
    /// <para><b>What the run then proves is that these folders are still there, and no more than
    /// that.</b> <see cref="PlanVerifier"/>'s stronger question, whether a protected directory
    /// survived but was emptied, is switched off for every plan holding a
    /// <see cref="RunCommandStep"/>: <see cref="RunReach.Of"/> marks such a run unbounded, because
    /// §5.1 hands the tool a command whose reach nothing here can state, and
    /// <c>PlanVerifier.WasEmptied</c> declines to draw a conclusion from that. So a cleanup that
    /// removed another account's folder outright is caught, and one that emptied it in place is not.
    /// That is a property of §5.6 against every command step in the product rather than of this
    /// provider, and it is stated here because Tier 3 is where it costs most.</para>
    /// </summary>
    /// <param name="unreadable">
    /// Whether a folder refused to be listed, so its children were never classified and the
    /// survivors below it are not the whole set.
    /// </param>
    private IReadOnlyList<(string Path, string Reason)> ProtectedNeighboursOf(
        FileHistoryTarget target,
        out bool unreadable)
    {
        var survivors = new List<(string Path, string Reason)>
        {
            (target.FileHistoryRoot,
                "The File History folder itself, which holds every account's backups on this drive."),
            (target.UserDirectory,
                "Your own File History, which holds a folder for every machine you back up."),
            (target.MachineDirectory, "This machine's File History."),
            (target.ConfigurationDirectory,
                "The catalogue that makes your saved versions restorable. Without it the versions "
                + "are still on the drive and Windows cannot find them."),
            (_discovery.ConfigurationDirectory,
                "Your File History settings, which record what is protected and where it is saved."),
        };

        var accounts = Neighbours(
            target.FileHistoryRoot,
            target.UserDirectory,
            "Another account's File History, so it is not this user's to trim.");

        var machines = Neighbours(
            target.UserDirectory,
            target.MachineDirectory,
            "Your File History of a different machine, which this machine's cleanup does not cover.");

        unreadable = accounts.Unreadable || machines.Unreadable;
        survivors.AddRange(accounts.Paths);
        survivors.AddRange(machines.Paths);

        return survivors;
    }

    /// <summary>
    /// Every child of <paramref name="root"/> other than <paramref name="ours"/>, by name. A link is
    /// listed on the same terms as a directory: it is a child the user can see, and what it points
    /// at was never classified.
    /// </summary>
    private static (IReadOnlyList<(string Path, string Reason)> Paths, bool Unreadable) Neighbours(
        string root,
        string ours,
        string reason)
    {
        var scan = ChildDirectories.Under(root);

        return (
            [.. scan.Directories
                .Concat(scan.Links)
                .Select(child => LongPath.Display(child.FullName))
                .Where(path => !path.Equals(LongPath.Display(ours), StringComparison.OrdinalIgnoreCase))
                .Select(path => (path, reason))],
            scan.Unreadable);
    }
}
