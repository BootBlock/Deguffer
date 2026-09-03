using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The files waiting in every volume's Recycle Bin (3.6 GB across two volumes on the audited
/// machine, with the system drive holding nothing at all).
///
/// Every NTFS volume keeps its own <c>$Recycle.Bin</c>, and deleting a file on <c>D:</c> fills
/// <c>D:</c>'s bin rather than <c>C:</c>'s. Tools that empty the bin almost always mean the system
/// drive's, which is why space accumulates on the others unnoticed.
///
/// <para><b>Tier 3, and the first one Deguffer ships.</b> The contents are files the user deleted
/// and can still restore, which is §3's definition of recoverable user data — so this is offered,
/// never pre-selected, and never executed without a confirmation that says the loss is permanent.
/// Whether that confirmation is §7's typed phrase is the user's to set. Nothing here is
/// regenerable by anything: the tool that could put a file back is the Recycle Bin itself, and
/// emptying it is what removes that.</para>
///
/// <para><b>What a step targets, and why it is not the bin.</b> A volume's <c>$Recycle.Bin</c>
/// holds one directory per account, named by that account's security identifier, so the bin root is
/// a shared parent in exactly §5.2's sense — another person's deleted files sit beside this user's,
/// and <c>S-1-5-18</c>'s sit beside both. The root is therefore never a target and never removed.
/// Each step targets one volume's directory for <em>this</em> user's own SID, which is the only
/// child recognised, and every other child is Tier 4 by construction. Windows re-creates the
/// per-account directory the next time something is deleted to that volume, so the bin keeps
/// working exactly as before.</para>
///
/// <para>Per item would be the other candidate, and it is the wrong grain twice over. A bin holds
/// thousands of paired <c>$I</c> and <c>$R</c> entries whose metadata and content must go together,
/// and §7's age column wants a row a reader can act on — "you last deleted something on this drive
/// eight months ago" — not ten thousand of them.</para>
///
/// <para><b>§5.1 is answered rather than skipped.</b> Windows ships <c>SHEmptyRecycleBin</c>, which
/// takes a volume root — the grain this provider already works at — and it is deliberately not used.
/// §7 makes the preview the primary action, and a plan has to name what it will remove: this one
/// names one directory per volume, sized and dated, and §5.6 asserts what survived beside it. A
/// shell call names a volume, reports nothing back, and would leave both of those with nothing to
/// say. The second reason is §5.2 itself — the safety property here is "this account's directory,
/// never a sibling", and handing a whole volume to the shell puts that decision outside the code the
/// rule is checkable in. The cost is accepted rather than dismissed: a path deletion does not tell
/// the shell what changed, so a Recycle Bin window left open may show a stale picture until it
/// refreshes. That is a stale picture rather than a stale deletion, and it has not been observed
/// here.</para>
///
/// <para>The bin root was observed holding nothing but per-account directories, so unlike NVIDIA's
/// <c>accounts</c> there is no file beside the target to name explicitly. The children that
/// <em>are</em> there are asserted individually, which is the same protection by a different
/// route.</para>
/// </summary>
public sealed class RecycleBinProvider : CleanupProviderBase
{
    /// <summary>
    /// The bin's directory name on every volume. Windows writes it capitalised this way on NTFS and
    /// as <c>$RECYCLE.BIN</c> on FAT, which costs nothing here: every path comparison this provider
    /// makes is case-insensitive, as the filesystem is.
    /// </summary>
    private const string BinDirectoryName = "$Recycle.Bin";

    private readonly IVolumeInventory _volumes;

    /// <summary>
    /// The one child of a bin root this user may empty, or an empty set when the user cannot be
    /// identified.
    ///
    /// Built once from <see cref="IUserEnvironment.UserSecurityIdentifier"/> because a process
    /// cannot change the account it runs as. It is a <see cref="DisposableChildSet"/> like every
    /// other provider's despite holding a value learned at run time rather than a declaration: what
    /// §5.2 asks of it is unchanged, and the answer to "which children may this tool delete?" is
    /// still one table with one entry in it.
    /// </summary>
    private readonly DisposableChildSet _children;

    public RecycleBinProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _volumes = volumes ?? VolumeInventory.Current;
        _children = new DisposableChildSet(
            Environment.UserSecurityIdentifier is { } sid
                ?
                [
                    new ChildClassification(
                        sid,
                        SafetyTier.UserData,
                        "Files you deleted on this drive. They are still restorable from the Recycle Bin, "
                        + "and emptying it is what ends that."),
                ]
                : []);
    }

    public override string Id => "recycle-bin";

    public override string Name => "Recycle Bin";

    public override SafetyTier Tier => SafetyTier.UserData;

    public override string WhatHappensOnNextUse =>
        "Every file waiting in these Recycle Bins is destroyed, so nothing you deleted can be "
        + "restored any more. Deleting a file afterwards works exactly as it did before.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Windows",
        Publisher = "Microsoft",
        Purpose = "Every volume keeps its own Recycle Bin, so deleting a file on D: fills D:'s "
            + "bin rather than C:'s. Tools that empty the bin almost always mean the system "
            + "drive's, which is why space accumulates unnoticed on the others.",
        Recommendation = "These are files you deleted and can still restore, and emptying the bin "
            + "is what removes the one thing that could put them back. Deguffer removes your own "
            + "account's folder on each fixed volume and leaves every other account's alone.",
    };

    /// <summary>
    /// The bin roots this provider looks at. Exposed so tests can assert none of them is targeted.
    /// </summary>
    public IReadOnlyList<string> BinRoots => [.. CandidateBins()];

    /// <summary>
    /// What this provider recognises inside a bin root.
    ///
    /// Exposed so a test can hold the declaration to the tier the provider claims. A plan carries
    /// the provider's tier rather than the child's, and <see cref="SafetyTierExtensions.IsOfferable"/>
    /// admits Tier 1, 2 and 3 alike — so a child declared at Tier 1 would still be targeted, under a
    /// plan still marked Tier 3, with nothing downstream noticing that the declaration and the stakes
    /// it records disagree. <see cref="DisposableChildSet"/> says a provider whose own tier is
    /// narrower than what it offers owes itself exactly that test.
    /// </summary>
    public DisposableChildSet DisposableChildren => _children;

    /// <summary>
    /// Presence is this user's own bin existing on some volume, never a bin root existing: every
    /// Windows volume has a bin root, and reading that as a hit would report a source on every
    /// machine and then plan nothing on most of them. It is also how an unidentifiable user fails
    /// closed, since <see cref="_children"/> is then empty and there is no path to probe.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(RecognisedBinPaths().Any(LongPath.DirectoryExists));

    /// <summary>
    /// The volume list is remembered for the life of a pass, so a drive mounted while the app was
    /// open needs it dropped like every other cached view of the machine.
    /// </summary>
    public override void InvalidateCaches()
    {
        base.InvalidateCaches();
        _volumes.Invalidate();
    }

    public override async Task<CleanupPlan> PlanAsync(CancellationToken ct = default)
    {
        if (Environment.UserSecurityIdentifier is null)
        {
            // §5.2's unknown case, at the level of the account rather than the child: with no
            // identity to match, every bin on the machine belongs to someone this provider cannot
            // name. Saying so beats classifying each one as unrecognised, which would be true and
            // would not explain anything.
            return EmptyPlan(
                "Deguffer could not establish which Windows account it is running as, so it is "
                + "leaving every Recycle Bin alone rather than guessing which one is yours.");
        }

        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var declined = new List<(string Path, string Reason)>();
        var survivors = new List<(string Path, string Reason)>();
        var unreadable = false;

        foreach (var bin in CandidateBins())
        {
            ct.ThrowIfCancellationRequested();

            if (!LongPath.DirectoryExists(bin))
            {
                continue;
            }

            // Reached by name rather than through an enumeration, so it needs the check the
            // enumeration would otherwise have made: a junctioned bin root hands back ordinary
            // directories from the far side, one of which could carry this user's SID.
            if (LongPath.IsReparsePoint(bin))
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{bin}' alone: it is a link to somewhere else, and Deguffer does not "
                    + "look through a link."));
                declined.Add((bin, "A link rather than a directory, so what it points at was never classified."));
                continue;
            }

            survivors.Add((
                bin,
                "The volume's Recycle Bin itself must survive — only this user's own bin inside it is removed."));

            unreadable |= !CollectFrom(bin, targets, declined, notes, ct);
        }

        // A refused bin root must not be reported as an absent one. Presence was decided by probing
        // this user's own bin by full name, and a full path resolves through a parent the account
        // may not list — so "no volume holds a bin for this user" would contradict what this same
        // provider established one method earlier. The per-bin notes already name which root
        // refused; what the sentence below would add is the claim that must not be made.
        if (targets.Count == 0 && declined.Count == 0 && !unreadable)
        {
            return EmptyPlan("No volume on this machine holds a Recycle Bin for this user.");
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, ct).ConfigureAwait(false);

        if (measured.Note is { } scanNote)
        {
            notes.Add(scanNote);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect([.. survivors, .. declined]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
        };
    }

    /// <summary>
    /// §5.2 for one volume's bin: recognise this user's own directory, and say plainly what is
    /// being left behind.
    ///
    /// A declined child is protected by name as well as omitted. Here that matters more than
    /// anywhere else in the codebase, because a spared child and a targeted one are siblings of
    /// identical shape under one parent, distinguished by nothing but a string of digits — and the
    /// spared one holds another person's files.
    /// </summary>
    /// <returns>
    /// False where the bin root would not be listed, so the caller can keep the plan from claiming
    /// the volume holds nothing.
    /// </returns>
    private bool CollectFrom(
        string bin,
        List<DeletionTarget> targets,
        List<(string Path, string Reason)> declined,
        List<PlanNote> notes,
        CancellationToken ct)
    {
        var scan = ChildDirectories.Under(bin);

        if (scan.Unreadable)
        {
            notes.Add(UnreadableRoot.Note(bin));
            return false;
        }

        foreach (var link in scan.Links)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Leaving '{link.Name}' in {bin} alone: it is a link to somewhere else, and Deguffer "
                + "does not delete through a link."));
            declined.Add((
                LongPath.Display(link.FullName),
                "A link rather than a directory, so what it points at was never classified."));
        }

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var classification = _children.Classify(child.Name);
            var path = LongPath.Display(child.FullName);

            if (!classification.Tier.IsOfferable())
            {
                // Deliberately not the classification's own sentence. Everything here is
                // unrecognised by the same rule, so the reason worth giving is why *this* provider
                // refused it, which covers another account's bin and a stray directory alike.
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{child.Name}' in {bin} alone: it is not this user's own Recycle Bin."));
                declined.Add((path, "Not this user's own Recycle Bin, so it is not this user's to empty."));
                continue;
            }

            targets.Add(new DeletionTarget(path, classification.Reason, LastActivity(child)));
        }

        return true;
    }

    /// <summary>
    /// When this bin last gained or lost an entry, for §7's age column — "you last deleted
    /// something on this drive eight months ago" is the figure that decides whether to empty it.
    ///
    /// <para><b>The same question <see cref="DirectoryAge"/> answers, and the one subject where the
    /// directory's own timestamp is already the whole answer.</b> That rule reads the entries as well
    /// because a file rewritten in place moves its own timestamp and leaves the directory's alone.
    /// Nothing in a bin is ever rewritten in place: an entry arrives when something is deleted and
    /// goes when it is restored or purged, and both move the directory. The entries would also
    /// answer a different question if they were read — Windows preserves each deleted file's own
    /// timestamps, so their dates are when the files were last edited rather than when they were
    /// thrown away.</para>
    ///
    /// <para>So calling the shared rule here would enumerate every file in the bin, which is
    /// everything the user has deleted on the volume, to arrive at the timestamp already in hand.
    /// The value comes from the enumeration that produced <paramref name="bin"/> and costs no
    /// second look at the disk.</para>
    /// </summary>
    private static DateTime? LastActivity(DirectoryInfo bin) => bin.LastWriteTimeUtc;

    /// <summary>
    /// Where a bin would be on each volume worth looking at.
    ///
    /// Fixed volumes only, and the exclusions are deliberate rather than incidental. A network
    /// share has no bin at all — Windows deletes across one outright — so any <c>$RECYCLE.BIN</c>
    /// found there belongs to the server's own users. Removable media can be swapped between the
    /// preview and the clean, which would put a plan the user approved against one disc in front of
    /// another. Both under-reclaim, which is the safe direction to be wrong in.
    /// </summary>
    private IEnumerable<string> CandidateBins() =>
        _volumes.Volumes
            .Where(v => v is { Kind: DriveType.Fixed, IsReady: true })
            .Select(v => Path.Combine(v.RootPath, BinDirectoryName));

    /// <summary>
    /// Every path this provider could ever target, by declaration rather than by enumeration — so
    /// answering "is there anything here?" costs one existence check per volume and can never reach
    /// a child the recognised set does not name.
    /// </summary>
    private IEnumerable<string> RecognisedBinPaths() =>
        from bin in CandidateBins()
        from name in _children.DisposableNames
        select Path.Combine(bin, name);
}
