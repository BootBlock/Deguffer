using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Every row's §5.2 declaration read as one, so a row that classifies the children of a folder can
/// tell which of the children it spares another row offers.
///
/// <para><b>A child another row offers is not this row's survivor.</b> Two rows may each walk the
/// same folder with a table of their own: a VS Code user-data folder holds <c>Local State</c>, so
/// both Chromium rows walk it beside the two VS Code rows. A row that asserted every child its own
/// table spares would assert that the other row's caches survive, and a run with both ticked would
/// report a correct removal as a §5.6 failure. So the spared child is left out of the survivors and
/// is not counted as left alone. It is the other row's to take.</para>
///
/// <para><b>Decided from the other row's declared table, never from what a run happens to
/// target.</b> A rule in the other row that reached a name its table does not offer is still caught
/// here, and <see cref="Execution.RunReach"/> keeps its own rule of never excusing a missing protected
/// path because another row targeted it.</para>
///
/// <para><b>Read from <see cref="ICleanupProvider.ToolRoots"/> alone.</b> That is the union §7.1
/// reads, and each root in it recognises what its row's plan would offer. A root from
/// <see cref="ICleanupProvider.DiscoverToolRootsAsync"/> only ever adds a refusal, and some of those
/// recognise nearly everything (see <see cref="ToolRoot.Sparing"/>), so reading one as an offer would
/// drop survivors that nobody removes.</para>
///
/// <para>A row's own declarations never excuse its own survivors, because a child its table offers is
/// a target rather than a survivor. So the question needs no "other than me".</para>
///
/// <para><b>Asked while a plan is assembled, never from a walk or from a declaration.</b> Answering
/// reads every row's <see cref="ICleanupProvider.ToolRoots"/>, and a row may build those from its own
/// walk, so a question asked from inside either would ask a row for the declaration it is still
/// building. <see cref="LevelWalk.SurvivorsAsync"/> is the one place a walk's children meet it.</para>
///
/// <para><b>Asked from several plans at once, and awaited.</b> A pass plans several rows together,
/// and the first question reads every row's declaration, which takes seconds where a row builds its
/// own from a walk. That reading runs on a thread of the walk's own (<see cref="DeclaredRoots"/>), so
/// neither the plan that asked first nor one that asks meanwhile holds a pool thread for it: each
/// awaits the one reading rather than starting a second.</para>
/// </summary>
public sealed class RowDeclarations
{
    private readonly PassMemo<Task<Dictionary<string, List<ToolRoot>>>> _byFolder = new();
    private IReadOnlyList<ICleanupProvider> _rows = [];

    /// <summary>
    /// The rows whose declarations are consulted. <see cref="Execution.CleanupPlanner"/> admits its
    /// own, so this always names every row in the planner. Until then it names none, which excuses
    /// nothing: a row built alone asserts every child it spares.
    /// </summary>
    public void Admit(IReadOnlyList<ICleanupProvider> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        Volatile.Write(ref _rows, rows);
        _byFolder.Forget();
    }

    /// <summary>
    /// Drop the index. The rows rebuild their declarations for each planning pass, so an index kept
    /// across passes would miss a folder that appeared between them.
    /// </summary>
    public void Invalidate() => _byFolder.Forget();

    /// <summary>
    /// Whether some row declares <paramref name="folder"/>, a directory that is not a link, as one it
    /// removes from the directory it sits in.
    /// </summary>
    /// <param name="ct">
    /// Checked between rows while the set is read. A row's declaration cannot be stopped part-way, but
    /// the first question in a pass reads every row's, for whichever row asked.
    /// </param>
    public async Task<bool> OfferedByARowAsync(string folder, CancellationToken ct = default) =>
        Path.GetDirectoryName(folder) is { } parent
        && LongPath.Entry(parent) is { } key
        && (await ByFolderAsync(ct).ConfigureAwait(false)).TryGetValue(key, out var roots)
        && roots.Exists(root => root.Recognises(new ToolRootChild(Path.GetFileName(folder), ChildKind.Folder)));

    /// <summary>Built once per planning pass (G4), because a plan asks once per spared child.</summary>
    private async Task<Dictionary<string, List<ToolRoot>>> ByFolderAsync(CancellationToken ct)
    {
        // A row whose declaration asked this would wait for the reading it is part of, which never
        // ends and says nothing about why.
        if (DeclaredRoots.IsBeingRead)
        {
            throw new InvalidOperationException(
                "A row asked what other rows offer while its own declaration was being read.");
        }

        while (true)
        {
            var reading = _byFolder.Get(() => IndexAsync(Volatile.Read(ref _rows), ct));

            try
            {
                return await reading.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The plan that started the reading was stopped, and this one was not. The memo keeps
                // no stopped reading, so asking again starts one for this plan.
            }
        }
    }

    private static async Task<Dictionary<string, List<ToolRoot>>> IndexAsync(
        IReadOnlyList<ICleanupProvider> rows, CancellationToken ct)
    {
        var byFolder = new Dictionary<string, List<ToolRoot>>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in await DeclaredRoots.ReadAsync(rows, ct).ConfigureAwait(false))
        {
            if (LongPath.Configured(root.Path) is not { } key)
            {
                continue;
            }

            if (!byFolder.TryGetValue(key, out var roots))
            {
                byFolder[key] = roots = [];
            }

            roots.Add(root);
        }

        return byFolder;
    }
}
