using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The thumbnails and previews Capture One keeps for each catalog, inside the catalog, and for each
/// folder of images in a session, beside the images. One community measurement put 33,608 images at
/// a 2,560-pixel preview size at 51.8 GB of cache.
///
/// <para><b>Tier 2, and the reason is a feature rather than a caveat.</b> Capture One says anything
/// in a <c>Cache</c> folder can be deleted, and rebuilds it the next time an image is viewed. It can
/// rebuild it only from the original, though, and a catalog's offline browsing is built on this
/// cache on purpose: with the drive holding the originals disconnected, the catalog stays browsable
/// and its images can still be adjusted. Remove the cache and that stops without a word, leaving a
/// catalog of question marks until the drive returns. Nothing here can tell "the originals are on
/// this disk" from "the originals are on a shelf", so the cost is stated rather than guessed. Where
/// the originals are online, rebuilding previews for a large catalog is the re-rendering of every
/// image, which is §3's "re-indexing for minutes" as well.</para>
///
/// <para><b>Found through Capture One's own record.</b> <see cref="CaptureOneDocuments"/> reads the
/// catalogs and sessions Capture One lists, wherever they are, and each is proved on disk by its
/// database before anything in it is offered. <see cref="CaptureOneLayout"/> holds what is
/// recognised and why nothing else is.</para>
///
/// <para><b>§5.1 has no route to drive.</b> Capture One has no command to empty its cache. Its
/// documented route is <b>Regenerate Previews</b> on selected images, a menu item in the running
/// program, and since version 16.3 that also shrinks the previews an older version made.</para>
/// </summary>
public sealed class CaptureOneCacheProvider : CleanupProviderBase
{
    private const string CatalogReason =
        "A Capture One catalog. Only its previews and thumbnails are removed.";

    private const string SessionReason =
        "A Capture One session, holding your images. Only the previews and thumbnails in it are removed.";

    /// <summary>
    /// The one column every item shares, as <see cref="ItemFacet"/> requires. The owner's own name
    /// already says which it is: a catalog's ends in <c>.cocatalog</c>.
    /// </summary>
    private const string OwnerFacet = "Catalog or session";

    private readonly ILiveTreeInspector _liveTrees;
    private CaptureOneDocumentList? _documents;

    public CaptureOneCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ILiveTreeInspector? liveTrees = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
        => _liveTrees = liveTrees ?? LiveTreeInspector.Default;

    public override string Id => "capture-one-cache";

    public override string Name => "Capture One previews and thumbnails";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override StepGrain Grain => StepGrain.Items;

    public override string WhatHappensOnNextUse =>
        "Capture One rebuilds each preview and thumbnail from the original the next time you view the "
        + "image, which for a large catalog takes a long time. A catalog whose originals are on a drive "
        + "that is not connected cannot rebuild them, so until you reconnect it you cannot browse or "
        + "adjust those images offline. Your photographs, your adjustments and the catalogs themselves "
        + "are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Capture One, the photo editor",
        Publisher = "Capture One A/S",
        Purpose = "Capture One keeps a thumbnail and a preview of every image, inside each catalog and "
            + "beside the images in each session, so that browsing does not wait on the raw files. The "
            + "previews also let you browse and adjust a catalog's images while the drive holding them "
            + "is disconnected.",
        Recommendation = "Capture One says anything in its cache can be deleted, and rebuilds it as you "
            + "view each image. Keep the cache of a catalog whose originals are offline, and choose "
            + "Regenerate Previews in Capture One to make an older catalog's previews smaller.",
    };

    /// <summary>
    /// §5.3. Capture One writes previews while it runs, and a catalog it has open is refused by the
    /// live-tree check when it holds the database. The warning covers what that check cannot see.
    /// </summary>
    protected override IReadOnlyList<string> ConflictingProcessNames => CaptureOneLayout.ProcessNames;

    private CaptureOneDocumentList Documents => _documents ??= CaptureOneDocuments.Read(Environment.LocalAppData);

    public override void InvalidateCaches()
    {
        _documents = null;
        _liveTrees.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present once Capture One has kept settings for this user. Whether a catalog it lists still
    /// holds previews is the plan's question: answering it here would walk every session twice.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default) =>
        Task.FromResult(Documents.Found);

    /// <summary>
    /// §5.2 as §7.1 reads it: each catalog folder and each sidecar folder recognises its
    /// <c>Cache</c> and nothing else. Found by reading Capture One's settings and walking each
    /// session, so it is asked here rather than declared.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var roots = new List<ToolRoot>();

        foreach (var catalog in Documents.Catalogs.Where(LongPath.DirectoryExists))
        {
            roots.Add(new ToolRoot(
                catalog,
                "This is a Capture One catalog, holding its database and often your photographs. "
                + "Deguffer removes only the Cache folder inside it.",
                CaptureOneLayout.IsCache));
        }

        foreach (var session in Documents.Sessions.Where(LongPath.DirectoryExists))
        {
            ct.ThrowIfCancellationRequested();

            roots.AddRange(CaptureOneLayout.WalkSession(session, ct).Sidecars.Select(sidecar => new ToolRoot(
                sidecar,
                "This is Capture One's folder for the images beside it, holding your adjustments to them. "
                + "Deguffer removes only the Cache folder inside it.",
                CaptureOneLayout.IsCache)));
        }

        return Task.FromResult<IReadOnlyList<ToolRoot>>(roots);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var documents = Documents;

        if (!documents.Found)
        {
            return EmptyPlan("Capture One has not been used by this user.");
        }

        var examination = new CaptureOneExamination();

        examination.Notes.AddRange(documents.Unread.Select(path => new PlanNote(
            PlanNoteSeverity.Warning,
            $"Deguffer could not read Capture One's settings at '{path}', so a catalog or session only "
            + "they list was neither cleared nor ruled out.")));

        foreach (var catalog in documents.Catalogs)
        {
            ct.ThrowIfCancellationRequested();
            CollectCatalog(catalog, examination);
        }

        foreach (var session in documents.Sessions)
        {
            ct.ThrowIfCancellationRequested();
            CollectSession(session, examination, ct);
        }

        if (examination.Disconnected.Count > 0)
        {
            examination.Notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Capture One lists {Count(examination.Disconnected.Count)} that {(examination.Disconnected.Count == 1 ? "is" : "are")} "
                + $"not on this computer now: {string.Join(", ", examination.Disconnected.Select(d => $"'{d}'"))}. "
                + "A drive that is not connected is the usual reason."));
        }

        var live = LiveTreeVeto.Apply(
            _liveTrees,
            [.. examination.Candidates.Select(c => new RecognisedBuildDirectory(c.Cache, c.Owner))],
            candidate => examination.LockFiles[candidate.Project],
            ct);

        if (LiveTreeVeto.NoteFor(live.Vetoed, vetoed => $"the previews for '{examination.OwnerOf(vetoed.Directory)}'") is { } held)
        {
            examination.Notes.Add(held);
        }

        if (LiveTreeVeto.IncompleteNote(live.Complete, "Close Capture One before you clean.") is { } incomplete)
        {
            examination.Notes.Add(incomplete);
        }

        var candidates = examination.Candidates.ToDictionary(c => c.Cache, StringComparer.OrdinalIgnoreCase);

        var (steps, measured) = await PlanDeletionsAsync(
            [
                .. live.Cleared.Select(cleared =>
                {
                    var found = candidates[cleared.Path];

                    return new DeletionTarget(
                        cleared.Path,
                        found.Reason,
                        DirectoryAge.Of(cleared.Path, ct),
                        Facets: [new ItemFacet(OwnerFacet, Path.GetFileName(found.Owner))],
                        Group: found.Owner,
                        UseCheck: cleared.StillUnused);
                }),
            ],
            keep,
            ct).ConfigureAwait(false);

        if (steps.Count == 0
            && examination.Declined.Count == 0
            && examination.Disconnected.Count == 0
            && live.Vetoed.Count == 0
            && !examination.Unreadable
            && documents.IsComplete)
        {
            return EmptyPlan(
                "None of the catalogs and sessions Capture One lists on this computer holds any previews.");
        }

        if (measured.Note is { } scanNote)
        {
            examination.Notes.Add(scanNote);
        }

        if (steps.Count > 0 && BuildRunningProcessNote() is { } running)
        {
            examination.Notes.Add(running);
        }

        return new CleanupPlan
        {
            ProviderId = Id,
            ProviderName = Name,
            Tier = Tier,
            WhatHappensOnNextUse = WhatHappensOnNextUse,
            Steps = steps,
            ProtectedPaths = Protect(
            [
                .. examination.Survivors,
                .. examination.Declined,
                .. live.Vetoed.Select(vetoed => (vetoed.Directory, LiveTreeVeto.ProtectedReason)),
            ]),
            Notes = examination.Notes,
            Fallback = measured.Fallback,
            // A catalog on a drive that is not connected was listed and never looked in, which is
            // a zero nobody can vouch for, as a declined one is.
            WasNotExamined = steps.Count == 0
                && (examination.Declined.Count > 0 || examination.Disconnected.Count > 0),
            HasUnreadableRoot = examination.Unreadable || !documents.IsComplete,
        };
    }

    private static bool Overlaps(string folder, string other) =>
        LongPath.Contains(folder, other) || LongPath.Contains(other, folder);

    private static string Count(int count) => count == 1 ? "a catalog or session" : $"{count} catalogs and sessions";

    /// <summary>
    /// One catalog: prove it by its database, then offer its <c>Cache</c> and name everything
    /// beside it. The catalog folder is taken as Capture One's record gives it, link or not, since
    /// that is the catalog Capture One opens; its <c>Cache</c> must not be a link.
    /// </summary>
    private static void CollectCatalog(string catalog, CaptureOneExamination examination)
    {
        if (!CaptureOneLayout.IsCatalogFolder(catalog))
        {
            examination.Decline(
                catalog,
                "Capture One's settings name a catalog database here, but the folder is not a Capture One "
                + "catalog, so nothing in it is offered.");
            return;
        }

        if (!examination.Reached(catalog) || examination.EntriesOf(catalog) is not { } entries)
        {
            return;
        }

        var databases = CaptureOneLayout.Databases(entries, CaptureOneLayout.CatalogDatabaseExtension);

        if (databases.Count == 0)
        {
            examination.Decline(
                catalog,
                "Capture One lists this as a catalog, but no catalog database is inside it, so it was not "
                + "recognised as one and is left alone.");
            return;
        }

        if (examination.CacheIn(catalog, entries) is not { } cache)
        {
            return;
        }

        examination.Survivors.Add((catalog, CatalogReason));
        examination.Survivors.AddRange(entries
            .Where(entry => !CaptureOneLayout.IsCache(entry.Name))
            .Select(entry => (LongPath.Display(entry.FullName), CaptureOneLayout.CatalogSurvivorReason(entry))));

        examination.LockFiles[catalog] = [.. databases, Path.Combine(catalog, CaptureOneLayout.CatalogWriteLock)];
        examination.Candidates.Add(new CaptureOneCandidate(
            cache,
            catalog,
            $"Previews and thumbnails for the catalog {Path.GetFileNameWithoutExtension(catalog)}. Capture "
            + "One rebuilds them from the originals as you view each image."));
    }

    /// <summary>
    /// One session: prove it by its database, then walk it for the <c>CaptureOne</c> folder beside
    /// each folder of images, and offer the <c>Cache</c> in each.
    /// </summary>
    private void CollectSession(string session, CaptureOneExamination examination, CancellationToken ct)
    {
        // A session file at or above the profile would make everything it holds a session to walk.
        // One anywhere in an application-data folder is no photographer's session either, and a walk
        // there would reach Capture One's own styles and presets.
        if (LongPath.Contains(session, Environment.UserProfile)
            || Overlaps(session, Environment.LocalAppData)
            || Overlaps(session, Environment.RoamingAppData))
        {
            examination.Decline(
                session,
                "Capture One lists a session here, but the folder holds your profile or its application "
                + "data, so it is not searched and nothing in it is offered.");
            return;
        }

        if (!examination.Reached(session) || examination.EntriesOf(session) is not { } entries)
        {
            return;
        }

        var databases = CaptureOneLayout.Databases(entries, CaptureOneLayout.SessionDatabaseExtension);

        if (databases.Count == 0)
        {
            examination.Decline(
                session,
                "Capture One lists a session here, but its session file is not in this folder, so it was "
                + "not recognised as one and is left alone.");
            return;
        }

        var walk = CaptureOneLayout.WalkSession(session, ct);
        var offered = false;

        foreach (var directory in walk.Unreadable)
        {
            examination.Notes.Add(UnreadableRoot.Note(directory));
            examination.Unreadable = true;
        }

        foreach (var link in walk.Links)
        {
            examination.DeclineLink(link);
        }

        foreach (var sidecar in walk.Sidecars)
        {
            ct.ThrowIfCancellationRequested();

            if (examination.EntriesOf(sidecar) is not { } sidecarEntries
                || examination.CacheIn(sidecar, sidecarEntries) is not { } cache)
            {
                continue;
            }

            if (!sidecarEntries.Any(entry => entry is DirectoryInfo && CaptureOneLayout.IsSettingsFolder(entry.Name)))
            {
                examination.Decline(
                    sidecar,
                    "No Capture One settings folder is beside its Cache, so it was not recognised as "
                    + "Capture One's and is left alone.");
                continue;
            }

            var images = Path.GetDirectoryName(sidecar)!;

            examination.Survivors.Add((images, "A folder of your images. Only Capture One's previews of them are removed."));
            examination.Survivors.Add((
                sidecar,
                "Capture One's record of the images beside it, holding your adjustments. Only its Cache is removed."));
            examination.Survivors.AddRange(sidecarEntries
                .Where(entry => !CaptureOneLayout.IsCache(entry.Name))
                .Select(entry => (LongPath.Display(entry.FullName), CaptureOneLayout.SidecarSurvivorReason(entry))));

            examination.Candidates.Add(new CaptureOneCandidate(
                cache,
                session,
                $"Previews and thumbnails for the images in '{Path.GetRelativePath(Path.GetDirectoryName(session) ?? session, images)}'. "
                + "Capture One rebuilds them from the images beside them as you view each one."));
            offered = true;
        }

        if (offered)
        {
            examination.Survivors.Add((session, SessionReason));
            examination.Survivors.AddRange(databases.Select(database => (
                database,
                "The session's own file: its collections, ratings and settings.")));
            examination.LockFiles[session] = databases;
        }
    }
}
