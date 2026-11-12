using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The folders a Code - OSS editor left in its extensions folder from an install that never finished,
/// or from a removal it never completed (3,455 MB across nine folders on the measured machine, every
/// one a full copy of an extension build older than the one installed).
///
/// <para><b>What the editor leaves, from its own source.</b> It extracts each extension into
/// <c>.&lt;uuid&gt;</c> and then renames that folder to <c>&lt;publisher&gt;.&lt;name&gt;-&lt;version&gt;</c>.
/// Where the extraction or the rename fails it deletes the folder and ignores any error, and where the
/// editor ends part way through it never tries, so a held file or a closed window leaves the folder
/// behind. The editor's start-up clean deletes only folders ending <c>.vsctmp</c>, which it makes by
/// renaming an extension it is removing, and its file watcher ignores every name starting with a dot,
/// so a <c>.&lt;uuid&gt;</c> folder is never loaded and never removed.</para>
///
/// <para><b>Tier 1.</b> The editor never reads these folders, the installed extension is a separate
/// folder this row never touches, and the marketplace supplies any build again. §5.1 has no command to
/// prefer: none of the editor's extension options touches a <c>.&lt;uuid&gt;</c> folder.</para>
///
/// <para><b>§5.2: two names, and everything else is Tier 4.</b> Inside a folder
/// <see cref="CodeExtensionsFolderDiscovery"/> identified, only a real directory named by
/// <see cref="UnfinishedInstall"/> or <see cref="SetAsideForRemoval"/> that the editor's list does not
/// name is offered. Every installed extension, the list, <c>.obsolete</c> and anything unrecognised is
/// named as a survivor, and the extensions folder itself never is a target.</para>
///
/// <para><b>Each folder goes whole or not at all, and only once nothing in it has changed for
/// <see cref="QuietDays"/> days.</b> The editor writes a <c>.&lt;uuid&gt;</c> folder only during an
/// extraction and at most two minutes of rename retries, so the age is what tells an abandoned folder
/// from an install in progress, and removing part of one in progress would break that install. The run
/// looks again on the disk before it removes each one (<see cref="DeleteDirectoryStep.IsAllOrNothing"/>),
/// and asks again whether a running program is using it.</para>
/// </summary>
public sealed partial class VsCodeExtensionLeftoversProvider : CleanupProviderBase
{
    /// <summary>How long nothing inside a folder must have changed before it is offered.</summary>
    public const int QuietDays = 7;

    /// <summary>The files beside the extensions that the editor keeps its own records in.</summary>
    private static readonly (string Name, string Reason)[] Records =
    [
        (CodeExtensionsFolderDiscovery.ListFile,
            "The editor's list of installed extensions, which is also how Deguffer identified this folder."),
        (".obsolete", "The editor's list of superseded extension builds it will remove itself."),
        (".init-default-profile-extensions", "The editor's record that it has set up the default profile's extensions."),
    ];

    private const string FolderReason =
        "The editor's extensions folder must survive; only the folders it left from unfinished installs and "
        + "removals are removed.";

    private const string InstalledReason = "An installed extension, which the editor's list names.";

    private const string UnrecognisedReason =
        "Not a folder the editor leaves from an unfinished install or removal, so it is left alone.";

    private readonly ISystemDirectories _system;
    private readonly ILiveTreeInspector _liveTrees;
    private readonly PassMemo<Survey> _survey = new();

    public VsCodeExtensionLeftoversProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null,
        ILiveTreeInspector? liveTrees = null,
        IVolumeInventory? volumes = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner,
            volumes: volumes)
    {
        _system = system ?? SystemDirectories.Current;
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
    }

    /// <summary>
    /// The folder an install extracts into: a dot, then a lower-case version-4 UUID in the 8-4-4-4-12
    /// form, which is what the editor's <c>generateUuid</c> writes whichever way it makes one.
    /// Case-sensitive, because nothing else the editor writes has that shape.
    /// </summary>
    [GeneratedRegex(
        @"\A\.[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z",
        RegexOptions.CultureInvariant)]
    internal static partial Regex UnfinishedInstall();

    /// <summary>
    /// The folder a removal renames an extension to before it deletes it:
    /// <c>&lt;folder&gt;.&lt;hash&gt;.vsctmp</c>, where the hash is a signed 32-bit number written in
    /// hexadecimal, so it may carry a minus sign.
    /// </summary>
    [GeneratedRegex(@"\A.+\.-?[0-9a-f]{1,8}\.vsctmp\z", RegexOptions.CultureInvariant)]
    internal static partial Regex SetAsideForRemoval();

    public override string Id => "vscode-extension-leftovers";

    public override string Name => "VS Code unfinished extension installs";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override StepGrain Grain => StepGrain.Parts;

    public override string WhatHappensOnNextUse =>
        "Nothing changes in the editor. It never loads these folders, and your installed extensions are "
        + "separate folders that are left alone.";

    public override ProviderBasis Basis => ProviderBasis.Observed;

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "VS Code and the editors built on it, such as VSCodium and Cursor",
        Publisher = "Microsoft, and each editor's own publisher",
        Purpose = "The editor unpacks each extension it installs into a temporary folder beside your "
            + "extensions and then renames it. When that fails, or the editor closes part way through, "
            + "the temporary folder stays, holding a full copy of the extension that the editor never "
            + "loads and never removes.",
        Recommendation = "Nothing here is in use. Deguffer removes only these leftover folders, and only "
            + $"once nothing in one has changed for {QuietDays} days, so an install in progress is left "
            + "alone. An extensions folder moved with --extensions-dir, or a portable install's, cannot be "
            + "found from outside the editor and is not looked at.",
    };

    /// <summary>
    /// §5.2 for every extensions folder found: only the two leftover names are recognised, and never a
    /// folder the editor's list names.
    /// </summary>
    public override IReadOnlyList<ToolRoot> ToolRoots =>
    [
        .. Look().Folders.Select(folder => ToolRoot.Folders(
            folder.Folder.Path,
            $"This is where {folder.Folder.Source} keeps your installed extensions. Deguffer removes only "
            + "the folders the editor left from installs and removals it never finished.",
            name => Recognise(name, folder.Folder.Listed) is not null)),
    ];

    /// <summary>
    /// The folder <see cref="CodeExtensionsFolderDiscovery.Variable"/> names where it is declined, such as
    /// a drive root, refusing only what the editor writes there: its records, and every extension its
    /// list names. Everything beside them is somebody else's, so nothing else is refused.
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ToolRoot>>(
            Look(ct).Search.DeclinedVariableFolder is { } declined
                ?
                [
                    ToolRoot.Sparing(
                        declined,
                        $"{CodeExtensionsFolderDiscovery.Variable} names this folder as the place the editor "
                        + "installs extensions. Deguffer does not treat the folder as the editor's, and leaves "
                        + "alone what the editor wrote in it.",
                        [
                            .. Records.Select(record => record.Name),
                            .. CodeExtensionsFolderDiscovery.Listed(declined) ?? new HashSet<string>(),
                        ]),
                ]
                : []);

    /// <summary>Each leftover folder by its own name, which is unique, and nothing else in the extensions folder.</summary>
    public override Task<IReadOnlyList<CleanedPlace>> CleanedPlacesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CleanedPlace>>(
        [
            .. from folder in Look(ct).Folders
               let names = folder.Children.Directories
                   .Where(child => Recognise(child.Name, folder.Folder.Listed) is not null)
                   .Select(child => child.Name)
                   .ToList()
               where names.Count > 0
               select CleanedPlace.FoldersNamed(folder.Folder.Path, names),
        ]);

    public override void InvalidateCaches()
    {
        _survey.Forget();
        _liveTrees.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>
    /// A leftover of any age, or something the search could not look past. An editor with nothing left
    /// over is no row: every machine with VS Code has an extensions folder.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var survey = Look(ct);

        return Task.FromResult(
            survey.Search.Unreadable
            || survey.Search.Declined.Count > 0
            || survey.Search.Notes.Count > 0
            || survey.Folders.Any(folder =>
                folder.Children.Unreadable
                || folder.Children.Directories.Any(child => Recognise(child.Name, folder.Folder.Listed) is not null)
                || folder.Children.Links.Any(link => Recognise(link.Name, folder.Folder.Listed) is not null)));
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var survey = Look(ct);

        if (survey.Folders.Count == 0 && survey.Search.Declined.Count == 0 && survey.Search.Notes.Count == 0)
        {
            return EmptyPlan(
                "No editor built on VS Code keeps an extensions folder in your profile, or in the folder "
                + $"{CodeExtensionsFolderDiscovery.Variable} names. A folder moved with --extensions-dir, "
                + "or a portable install's, cannot be found from outside the editor and is not looked at.");
        }

        // Fixed once, here, so the preview and the removal agree about which files are recent however
        // long the preview sits on screen, the reason TempDirectoryProvider fixes its floor.
        var floor = MinimumAge.Within(TimeSpan.FromDays(QuietDays), DateTime.UtcNow);
        var effective = MinimumAge.Stricter(keep, floor);

        var notes = new List<PlanNote>(survey.Search.Notes);
        var survivors = new List<(string Path, string Reason)>(survey.Search.Declined);
        var candidates = new Dictionary<string, Leftover>(StringComparer.OrdinalIgnoreCase);
        var unreadable = survey.Search.Unreadable;
        var recent = 0;

        foreach (var (folder, children) in survey.Folders)
        {
            ct.ThrowIfCancellationRequested();

            survivors.Add((folder.Path, FolderReason));
            survivors.AddRange(Records.Select(record => (Path.Combine(folder.Path, record.Name), record.Reason)));

            if (children.Unreadable)
            {
                notes.Add(UnreadableRoot.Note(folder.Path));
                unreadable = true;
                continue;
            }

            foreach (var link in children.Links)
            {
                var path = LongPath.Display(link.FullName);

                survivors.Add((path, CacheLevelWalk.LinkReason));

                if (Recognise(link.Name, folder.Listed) is not null)
                {
                    notes.Add(CacheLevelWalk.Note(path));
                }
            }

            foreach (var child in children.Directories)
            {
                var path = LongPath.Display(child.FullName);

                if (Recognise(child.Name, folder.Listed) is not { } kind)
                {
                    survivors.Add((path, folder.Listed.Contains(child.Name) ? InstalledReason : UnrecognisedReason));
                }
                else if (effective.Protects(child))
                {
                    // The folder's own times, which an extraction moves as it adds each entry at the top,
                    // and which the measurement below cannot see for an empty folder.
                    recent++;
                }
                else
                {
                    candidates.Add(path, new Leftover(folder, kind));
                }
            }
        }

        // An extension the editor is running runs from its installed folder, never from one of these, so
        // anything using a leftover is something else, such as a terminal or a search left open in it.
        var live = LiveTreeVeto.Apply(
            _liveTrees,
            [.. candidates.Keys.Select(path => new RecognisedBuildDirectory(path, path))],
            lockFiles: [],
            ct);

        if (LiveTreeVeto.NoteFor(live.Vetoed, vetoed => Path.GetFileName(vetoed.Directory)) is { } held)
        {
            notes.Add(held);
        }

        if (LiveTreeVeto.UncheckedNote(live.Unchecked.Count) is { } incomplete)
        {
            notes.Add(incomplete);
        }

        survivors.AddRange(live.Survivors);

        var targets = live.Cleared.Select(cleared =>
        {
            var leftover = candidates[cleared.Path];

            return new DeletionTarget(
                cleared.Path,
                leftover.Kind == LeftoverKind.UnfinishedInstall
                    ? "An extension install the editor started and never finished. The editor never loads "
                        + "this folder, and the installed extension is a separate folder."
                    : "An extension the editor set aside to remove and never finished removing. The editor "
                        + "no longer uses it.",
                DatedOneLevelDown: true,

                // Nothing creates the folder again, so removing it is the whole of what the row is for.
                IsLeftover: true,
                Group: leftover.Folder.Source,
                UseCheck: cleared.StillUnused);
        }).ToList();

        var (planned, measured) = await PlanDeletionsAsync(targets, effective, ct).ConfigureAwait(false);

        var steps = new List<CleanupStep>(planned.Count);

        foreach (var step in planned.Cast<DeleteDirectoryStep>())
        {
            if (step.WithheldRecent)
            {
                recent++;
            }
            else
            {
                steps.Add(step with { IsAllOrNothing = true });
            }
        }

        // A recent folder is not asserted to survive. An install in progress renames its folder within
        // minutes, and the editor's next start removes a recent .vsctmp, so asserting across an
        // indefinite preview that either is still there would raise §5.6's alarm on an ordinary run.
        if (recent > 0)
        {
            notes.Add(new PlanNote(
                PlanNoteSeverity.Information,
                $"Left {(recent == 1 ? "one folder" : $"{recent} folders")} alone that the editor wrote to in "
                + $"the last {effective.Describe()}. An install in progress writes into one, so a folder is "
                + $"offered only once nothing in it has changed for {floor.Describe()}."));
        }

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
            ProtectedPaths = await ProtectAsync([.. survivors], ct).ConfigureAwait(false),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = unreadable,
            WasNotExamined = steps.Count == 0 && (survey.Search.Declined.Count > 0 || live.Held.Count > 0),

            // The floor travels on the plan, so the run's look at the disk judges a file written since the
            // preview by the same cut-off. CleanupProviderBase.PlanAsync will not loosen it.
            Keep = floor,
        };
    }

    /// <summary>
    /// Which leftover <paramref name="name"/> is, or null where it is not one. A name the editor's list
    /// gives is an installed extension, whatever its shape.
    /// </summary>
    private static LeftoverKind? Recognise(string name, IReadOnlySet<string> listed) =>
        listed.Contains(name) ? null
        : UnfinishedInstall().IsMatch(name) ? LeftoverKind.UnfinishedInstall
        : SetAsideForRemoval().IsMatch(name) ? LeftoverKind.SetAsideForRemoval
        : null;

    /// <summary>
    /// The extensions folders and what each holds, read once a planning pass (G4): presence, the
    /// declarations and the plan all ask.
    /// </summary>
    private Survey Look(CancellationToken ct = default) => _survey.Get(() =>
    {
        var search = CodeExtensionsFolderDiscovery.Search(Environment, _system, Volumes, ct);

        return new Survey(
            search,
            [.. search.Folders.Select(folder => new FolderSurvey(folder, ChildDirectories.Under(folder.Path)))]);
    });

    private enum LeftoverKind
    {
        UnfinishedInstall,
        SetAsideForRemoval,
    }

    private sealed record Leftover(CodeExtensionsFolder Folder, LeftoverKind Kind);

    private sealed record FolderSurvey(CodeExtensionsFolder Folder, ChildDirectoryScan Children);

    private sealed record Survey(CodeExtensionsSearch Search, IReadOnlyList<FolderSurvey> Folders);
}
