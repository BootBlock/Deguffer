using System.Collections.Frozen;
using Deguffer.Core.Cloud;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The local copies a sync app keeps of files that are safe in the cloud, which it can release while
/// leaving every file where it is (<c>docs/todo/unreached-locations.md</c> §10).
///
/// <para><b>Tier 2.</b> Nothing is lost, and a file released is downloaded again the next time it is
/// opened. That is §3's "regenerable, with cost", and offline it is a file that will not open until the
/// machine reconnects, so it is never pre-selected.</para>
///
/// <para><b>Recognised sync apps only (§5.2).</b> The mechanism is Windows' own and any sync app can
/// register a root, but what each does with a released file is the app's decision. A root whose app is
/// not in <see cref="RecognisedApps"/> is named in a note, protected, and left alone, as an unrecognised
/// child of a tool's root is.</para>
///
/// <para><b>The walk is the plan's, and it is never repeated at the clean.</b> The plan names the files
/// it chose, and the clean acts on those and no others. Each is judged again through the handle that
/// unpins it. See <see cref="ReleaseLocalCopiesStep"/>.</para>
/// </summary>
public sealed class CloudLocalCopiesProvider : CleanupProviderBase
{
    /// <summary>
    /// The sync apps whose releasing Deguffer relies on, keyed by the provider name their roots register
    /// under, with the name the user knows them by.
    ///
    /// <para><b>OneDrive alone, until another is observed.</b> OneDrive, and the SharePoint and Teams
    /// libraries its client syncs, register as <c>OneDrive!…</c>, and Microsoft documents unpinning as
    /// the route to online-only for that client. Nextcloud and Proton Drive use the same mechanism, by
    /// their own source and documentation, and join this set once a real root has shown the name each
    /// registers under. Dropbox, iCloud, Box, Google Drive and pCloud are unestablished or do not use
    /// placeholders at all.</para>
    /// </summary>
    public static readonly FrozenDictionary<string, string> RecognisedApps =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["OneDrive"] = "OneDrive",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public CloudLocalCopiesProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ICloudFiles? cloud = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default,
            cloud: cloud)
    {
    }

    public override string Id => "cloud-local-copies";

    public override string Name => "Local copies of cloud files";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "The files stay exactly where they are and open normally while you are online. Your sync app "
        + "releases their local copies in the background, so anything you open offline afterwards will "
        + "not be available until you reconnect.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "OneDrive, through Windows' own support for files kept in the cloud",
        Publisher = "Microsoft",
        Purpose = "Every file you have opened from OneDrive stays downloaded on this PC as well as in the "
            + "cloud. Windows can release those local copies and keep each file listed where it was, "
            + "which is what OneDrive's own \"Free up space\" does.",
        Recommendation = "Nothing is deleted, here or in the cloud. Files you chose to always keep on "
            + "this device, and files with changes not yet uploaded, are left as they are.",
    };

    /// <summary>
    /// A recognised root, or a list Windows would not give: a refusal is never read as absence, and the
    /// plan then says what happened.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Cloud.SyncRoots() is not { } roots
            || roots.Any(root => RecognisedApps.ContainsKey(root.ProviderName)));

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        if (Cloud.SyncRoots() is not { } roots)
        {
            return UnexaminedPlan("Windows would not list the folders kept in step with the cloud, so none of "
                + "them was looked at.") with
            {
                Notes =
                [
                    new PlanNote(
                        PlanNoteSeverity.Warning,
                        "Windows would not list the folders kept in step with the cloud, so none of them was "
                        + "looked at."),
                ],
            };
        }

        var steps = new List<ReleaseLocalCopiesStep>();
        var notes = new List<PlanNote>();
        var protect = new List<(string Path, string Reason)>();
        var declined = false;
        var unreadable = false;
        var recentOnly = new List<string>();

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();

            var name = NameOf(root);

            if (!RecognisedApps.TryGetValue(root.ProviderName, out var app))
            {
                declined = true;
                protect.Add((root.Path, "A folder an unrecognised sync app keeps in step with the cloud, which "
                    + "Deguffer leaves alone."));
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"{name} is kept in step by {root.ProviderName}, which Deguffer does not recognise, so its "
                    + "files are left as they are."));
                continue;
            }

            protect.Add((root.Path, $"The folder {app} keeps in step with the cloud, which must survive with "
                + "every file in it."));

            if (Cloud.ProviderState(root.Path) is not SyncProviderState.Running)
            {
                declined = true;
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Warning,
                    $"{app} is not running, so it would release nothing from {name}. Start it and scan again."));
                continue;
            }

            var selection = await Task.Run(() => PlaceholderWalk.Of(Cloud, root.Path, keep, ct), ct)
                .ConfigureAwait(false);

            notes.AddRange(NotesFor(selection, app, name));
            unreadable |= selection.Unreadable > 0;

            // Every candidate the rules left alone is one this plan declined, so a root that offers
            // nothing because of them is not clear. See CleanupPlan.WasNotExamined.
            declined |= selection.Held.Any(held => held.Key is not HeldBack.Recent && held.Value.Files > 0);

            var recent = selection.HeldFor(HeldBack.Recent);

            if (selection.Files.Count == 0)
            {
                // The guard's own note is added only to a plan with a step, so a root the guard emptied
                // says it here.
                if (recent.Files > 0)
                {
                    recentOnly.Add(root.Path);
                    notes.Add(new PlanNote(
                        PlanNoteSeverity.Information,
                        $"{Held(recent)} in {name} changed in the last {keep.Describe()}, so they stay, as you asked."));
                }

                continue;
            }

            steps.Add(new ReleaseLocalCopiesStep(root.Path, app, $"Local copies of files in {name}")
            {
                Files = selection.Files,
                Estimated = ScanSize.FromLengths(selection.Bytes),
                WithheldRecent = recent.Files > 0,
            });
        }

        if (steps.Count == 0 && notes.Count == 0)
        {
            return EmptyPlan("Nothing kept in the cloud has a local copy on this PC.");
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = [.. Protected(protect, recentOnly)],
            Notes = notes,
            WasNotExamined = declined && steps.Count == 0,
            HasUnreadableRoot = unreadable,
        };
    }

    /// <summary>
    /// Each root protected, and marked as holding recent files where those are the only reason it has no
    /// step, so the row does not read as clear about a folder full of local copies the guard kept.
    /// See <see cref="CleanupPlan.HasRecentContentHeldBack"/>.
    /// </summary>
    private static IEnumerable<ProtectedPath> Protected(
        List<(string Path, string Reason)> roots,
        List<string> recentOnly) =>
        Protect([.. roots]).Select(path => recentOnly.Contains(path.Path, StringComparer.OrdinalIgnoreCase)
            ? path with { Withheld = Withholding.TooRecent }
            : path);

    /// <summary>What the rules left alone in one root, said once per reason and only where it happened.</summary>
    private static IEnumerable<PlanNote> NotesFor(ReleaseSelection selection, string app, string name)
    {
        if (selection.HeldFor(HeldBack.Pinned) is { Files: > 0 } pinned)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"{Held(pinned)} in {name} you chose to always keep on this device stay as they are. Releasing "
                + "them would undo that choice, and the only way back is to download them again.");
        }

        if (selection.HeldFor(HeldBack.NotInSync) is { Files: > 0 } unsynced)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"{Held(unsynced)} in {name} with changes {app} has not uploaded yet stay as they are, so no "
                + "edit can be lost.");
        }

        if (selection.HeldFor(HeldBack.Excluded) is { Files: > 0 } excluded)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"{Held(excluded)} in {name} excluded from sync stay as they are, because the cloud may not "
                + "hold them.");
        }

        if (selection.HeldFor(HeldBack.AlreadyRequested) is { Files: > 0 } waiting)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Information,
                $"{Held(waiting)} in {name} are already waiting for {app} to release them.");
        }

        if (selection.Unreadable > 0)
        {
            yield return new PlanNote(
                PlanNoteSeverity.Warning,
                $"Windows would not describe {selection.Unreadable:N0} item(s) in {name}, so they and anything "
                + "inside them stay as they are.");
        }
    }

    private static string Held(HeldTally tally) =>
        $"{tally.Files:N0} file(s) holding {FreeSpace.Format(tally.Bytes)}";

    /// <summary>
    /// What the sync app calls the root, or its folder's name where the app registered a resource
    /// reference rather than a name.
    /// </summary>
    private static string NameOf(SyncRoot root) =>
        root.DisplayName is { Length: > 0 } display && !display.StartsWith('@')
            ? display
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(root.Path));
}
