using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// A row that offers what named tools left in this account's temporary folders, recognised by the
/// names those tools' own source gives them. Everything else in the folders is not this row's.
///
/// <para><b>Why a row per tier rather than one row.</b> A tier is a provider's, and what tools leave
/// in a temporary folder spans three: caches a tool rebuilds, installers it would have to download
/// again, and logs nobody can get back. Each derived row names its markers and its tier; the
/// examination, the claim on each entry and the plan are the same for all three, and are here.</para>
///
/// <para><b>This account's folders only.</b> <c>C:\Windows\Temp</c> is where services running as
/// the system write, and the checks these rows lean on answer for this account: a named mutex is
/// looked for in this logon session's namespace, and a service's Roslyn session would read as
/// dead from here. Nothing measured puts these tools' leftovers there.</para>
///
/// <para><b>Each entry is this row's whether or not it is offered today</b> — see
/// <see cref="ITemporaryFolderTenant"/> — so an entry a tool is still using is left alone here, said
/// so, asserted to survive, and not handed to the "Temporary files" row to take on its age.</para>
/// </summary>
public abstract class TempMarkerProviderBase : CleanupProviderBase, ITemporaryFolderTenant
{
    private readonly ISystemDirectories _system;
    private readonly ILiveTreeInspector _liveTrees;

    private TempRootSet? _roots;
    private TempMarkerFindings? _findings;

    protected TempMarkerProviderBase(
        IUserEnvironment? environment,
        IProcessRunner? runner,
        IProcessInspector? inspector,
        IDirectoryScanner? scanner,
        ISystemDirectories? system,
        ILiveTreeInspector? liveTrees)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _system = system ?? SystemDirectories.Current;
        _liveTrees = liveTrees ?? LiveTreeInspector.Default;
    }

    public override StepGrain Grain => StepGrain.Parts;

    /// <summary>The sentence a row with nothing to offer shows.</summary>
    protected abstract string NothingLeftBehind { get; }

    /// <summary>
    /// Where this row looks, and what it recognises there, given this account's temporary folders.
    /// </summary>
    protected abstract IReadOnlyList<TempMarkerPlace> PlacesIn(IReadOnlyList<string> accountFolders);

    /// <summary>
    /// What the row says about a setting that moved one of its places, given this account's
    /// temporary folders. Nothing for a row with no such setting.
    /// </summary>
    protected virtual IEnumerable<PlanNote> NotesFor(IReadOnlyList<string> accountFolders) => [];

    /// <summary>The machine's own directories, for a row that must keep a configured place out of them.</summary>
    protected ISystemDirectories Machine => _system;

    /// <summary>This account's own temporary folders, resolved once per planning pass.</summary>
    protected IReadOnlyList<string> AccountFolders =>
        (_roots ??= TempRoots.Resolve(Environment, _system)).AccountFolders;

    public override void InvalidateCaches()
    {
        _roots = null;
        _findings = null;
        _liveTrees.Invalidate();
        base.InvalidateCaches();
    }

    /// <summary>
    /// Present where a tool this row knows has left anything in a temporary folder, or where a folder
    /// would not be read. The second is what keeps the note saying so reachable: the planner never
    /// asks an absent provider for a plan.
    /// </summary>
    public override Task<bool> IsPresentAsync(CancellationToken ct = default)
    {
        var findings = Examine(ct);
        return Task.FromResult(findings.Recognised.Count > 0 || findings.Unreadable);
    }

    public Task<IReadOnlyList<string>> ClaimedEntriesAsync(
        IReadOnlyList<string> folders,
        CancellationToken ct = default) =>
        Task.FromResult(Examine(ct).ClaimsIn(folders));

    /// <summary>
    /// Explore's reading of the same examination: an entry held back is refused, and in a tool's own
    /// folder only what the plan would take is recognised.
    ///
    /// <para>Read again whenever Explore rebuilds its policy, because what is running changes and an
    /// answer kept for the life of the process would say a finished build is still going on.</para>
    /// </summary>
    public override Task<IReadOnlyList<ToolRoot>> DiscoverToolRootsAsync(CancellationToken ct = default)
    {
        var findings = TempMarkerSurvey.Examine(PlacesIn(AccountFolders), Inspector, _liveTrees, ct);
        var taken = new HashSet<string>(findings.Targets.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);

        return Task.FromResult<IReadOnlyList<ToolRoot>>(
        [
            .. findings.OwnedPlaces.Select(place => new ToolRoot(
                place.Directory,
                $"This folder belongs to {place.Owner}. Deguffer removes only what it recognises in it "
                + "as left behind, and leaves the rest alone.",
                name => taken.Contains(Path.Combine(place.Directory, name)))),
            .. findings.HeldBack.Select(held => new ToolRoot(
                held,
                "The tool that left this in the temporary folder may still be using it, so Deguffer "
                + "leaves it alone.",
                static _ => false)),
        ]);
    }

    protected override async Task<CleanupPlan> BuildPlanAsync(MinimumAge keep, CancellationToken ct)
    {
        var findings = Examine(ct);
        var settings = NotesFor(AccountFolders).ToList();

        if (findings.Targets.Count == 0 && findings.Declined == 0 && !findings.Unreadable)
        {
            var empty = EmptyPlan(NothingLeftBehind);
            return empty with
            {
                ProtectedPaths = Protect([.. findings.Survivors]),
                Notes = [.. empty.Notes, .. settings, .. findings.Notes],
            };
        }

        var (steps, measured) = await PlanDeletionsAsync(findings.Targets, keep, ct).ConfigureAwait(false);
        var notes = new List<PlanNote>([.. settings, .. findings.Notes]);

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
            ProtectedPaths = Protect([.. findings.Survivors]),
            Notes = notes,
            Fallback = measured.Fallback,
            HasUnreadableRoot = findings.Unreadable,
            WasNotExamined = findings.Targets.Count == 0 && findings.Declined > 0,
        };
    }

    /// <summary>The examination for this planning pass, shared by the plan, the presence probe and the claim.</summary>
    private TempMarkerFindings Examine(CancellationToken ct) =>
        _findings ??= TempMarkerSurvey.Examine(PlacesIn(AccountFolders), Inspector, _liveTrees, ct);
}
