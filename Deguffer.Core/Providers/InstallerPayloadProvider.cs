using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What an installer unpacks or downloads into a folder of its own and leaves behind once the thing
/// it installed is running from somewhere else.
///
/// <para>Each vendor's own knowledge is in the <see cref="InstallerPayloadRoot"/> rows its provider
/// declares. This class holds only what the rows share, which is the §5.2 argument: those folders sit
/// at the top of the system drive or in <c>%PROGRAMDATA%</c>, so the drive is never listed, every
/// folder on the way down is reached by name and checked for being a link, and only the payload
/// folder itself is listed. Written once because a second copy of the walk is the one that would
/// eventually lose the link check, for the reason <see cref="DeclaredLocations"/> gives.</para>
///
/// <para><b>Presence is a payload with something in it, never a folder existing.</b> An installer
/// that tidies up after itself removes the files and can leave the folders that held them, and a
/// vendor can keep its folder for something else entirely. A row for either would tell the user
/// there is something to reclaim when there is not.</para>
/// </summary>
public abstract class InstallerPayloadProvider : CleanupProviderBase
{
    private const string LinkReason =
        "A link rather than a directory, so what it points at was never classified.";

    private readonly IReadOnlyList<InstallerPayloadRoot> _roots;

    /// <param name="candidates">
    /// The rows this provider declares. Only those whose base is a full path are kept: Windows
    /// answers an empty string for a folder it cannot locate, and a folder named below an empty base
    /// would be a path relative to Deguffer's own working directory, which is a directory nobody
    /// pointed at. §5.2's direction is to reach nothing there, never to guess.
    /// </param>
    protected InstallerPayloadProvider(
        IUserEnvironment environment,
        IProcessRunner runner,
        IProcessInspector inspector,
        IDirectoryScanner scanner,
        IEnumerable<InstallerPayloadRoot> candidates)
        : base(environment, runner, inspector, scanner)
    {
        _roots = [.. candidates.Where(root => Path.IsPathFullyQualified(root.Base))];
    }

    public override StepGrain Grain => StepGrain.Items;

    /// <summary>What one payload is called in a sentence, in the plural: "driver packages".</summary>
    protected abstract string Payloads { get; }

    /// <summary>What the user is told when none of the folders holds a payload.</summary>
    protected abstract string NothingFound { get; }

    /// <inheritdoc />
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. _roots.Select(root => new ToolRoot(
            root.Path,
            $"This is the {root.Label} folder. Deguffer removes the {Payloads} it recognises inside "
            + "it and nothing else, because anything else in it may be something another program needs.",
            root.Recognises)),
    ];

    /// <summary>
    /// A recognised payload that may hold something. One listing per folder, and only of the folders
    /// the rows name, so answering never reaches the top of the drive.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        foreach (var root in _roots)
        {
            ct.ThrowIfCancellationRequested();

            switch (LongPath.ProbeDirectory(root.Path))
            {
                case PathPresence.Refused:
                    return Task.FromResult(true);

                case PathPresence.Absent:
                    continue;
            }

            var scan = ChildDirectories.Under(root.Path);

            if (scan.Unreadable
                || scan.Directories.Any(child =>
                    root.Recognises(child.Name) && DirectoryContent.MayBePresent(child.FullName)))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var notes = new List<PlanNote>();
        var targets = new List<DeletionTarget>();
        var survivors = new List<(string Path, string Reason)>();
        var links = 0;
        var declined = 0;
        var unreadable = false;

        foreach (var root in _roots)
        {
            ct.ThrowIfCancellationRequested();

            switch (Reach(root, survivors, notes))
            {
                case Reached.Refused:
                    unreadable = true;
                    continue;

                case Reached.Link:
                    links++;
                    continue;

                case Reached.Absent:
                    continue;
            }

            var scan = ChildDirectories.Under(root.Path);

            // Found by name a moment ago, and a listing right is separate from a traverse right. A
            // refusal here would otherwise leave a plan with no steps and nothing said, which the
            // shell renders as "Already clear" about a folder nobody read.
            if (scan.Unreadable)
            {
                notes.Add(UnreadableRoot.Note(root.Path));
                unreadable = true;
                continue;
            }

            foreach (var link in scan.Links)
            {
                links++;
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{root.Label}\\{link.Name}' alone: it is a link to somewhere else, and "
                    + "Deguffer does not delete through a link."));
                survivors.Add((LongPath.Display(link.FullName), LinkReason));
            }

            foreach (var child in scan.Directories)
            {
                ct.ThrowIfCancellationRequested();

                var classification = root.Classify(root.Path, child.Name);
                var path = LongPath.Display(child.FullName);

                if (!classification.Tier.IsOfferable())
                {
                    // Protected by name as well as left out: the declined and the targeted are
                    // siblings under one parent, which is exactly when an over-broad rule takes both.
                    declined++;
                    notes.Add(new PlanNote(
                        PlanNoteSeverity.Information,
                        $"Leaving '{root.Label}\\{child.Name}' alone: {classification.Reason}"));
                    survivors.Add((path, classification.Reason));
                    continue;
                }

                // An installer that tidied up removed the files and left the folders. Offering one
                // would be a row with nothing in it, so it is neither offered nor mentioned.
                if (!DirectoryContent.MayBePresent(path))
                {
                    continue;
                }

                targets.Add(new DeletionTarget(
                    path,
                    classification.Reason,
                    DirectoryAge.Of(path, ct),
                    Identity: new ItemIdentity($"{root.Label}\\{child.Name}", $"{root.Vendor} {child.Name}"),
                    Facets: [new ItemFacet("Vendor", root.Vendor)],
                    Group: root.Vendor));
            }
        }

        if (targets.Count == 0 && links == 0 && declined == 0 && !unreadable)
        {
            return EmptyPlan(NothingFound);
        }

        var (steps, measured) = await PlanDeletionsAsync(targets, keep, ct).ConfigureAwait(false);

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
            ProtectedPaths = Protect([.. Deduplicate(survivors)]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
            WasNotExamined = targets.Count == 0 && links > 0,
        };
    }

    private enum Reached
    {
        Present,
        Absent,
        Refused,
        Link,
    }

    /// <summary>
    /// Walk from the base to the payload folder by name, protecting each folder passed through.
    ///
    /// The walk is the point, for the reason <see cref="DeclaredLocations"/> gives: a junctioned
    /// <c>C:\NVIDIA</c> would hand the listing below the far side's folders, and a check on the payload
    /// folder alone would never see it. Survivors are committed only once the folder is reached, so
    /// §5.6 names the folders something could actually be taken out of.
    /// </summary>
    private Reached Reach(
        InstallerPayloadRoot root,
        List<(string Path, string Reason)> survivors,
        List<PlanNote> notes)
    {
        var passed = new List<(string Path, string Reason)>
        {
            (root.Base, $"The folder this sits in must survive. It is never listed, and only the "
                + $"{Payloads} named inside it are removed."),
        };

        foreach (var path in (IEnumerable<string>)[.. root.Containers, root.Path])
        {
            switch (LongPath.ProbeDirectory(path))
            {
                case PathPresence.Refused:
                    notes.Add(UnreadableRoot.UnreachedNote(LongPath.Display(path)));
                    survivors.Add((path, UnreadableRoot.UnreachedReason));
                    return Reached.Refused;

                case PathPresence.Absent:
                    return Reached.Absent;
            }

            if (LongPath.IsReparsePoint(path))
            {
                notes.Add(new PlanNote(
                    PlanNoteSeverity.Information,
                    $"Leaving '{LongPath.Display(path)}' alone: it is a link to somewhere else, and "
                    + "Deguffer does not look through a link."));
                survivors.Add((path, LinkReason));
                return Reached.Link;
            }

            passed.Add((
                path,
                $"The {Path.GetFileName(path)} folder itself must survive. Only the {Payloads} "
                + "recognised inside it are removed."));
        }

        survivors.AddRange(passed);
        survivors.AddRange(root.ProtectedNames.Select(p => (Path.Combine(root.Path, p.Name), p.Reason)));

        return Reached.Present;
    }

    /// <summary>One entry per path, keeping the first reason. Two roots can share the drive they sit on.</summary>
    private static IEnumerable<(string Path, string Reason)> Deduplicate(
        IEnumerable<(string Path, string Reason)> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return paths.Where(p => seen.Add(p.Path));
    }
}
