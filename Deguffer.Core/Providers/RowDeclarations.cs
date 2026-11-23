using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The §5.2 declarations of the other rows, so a row that classifies the children of a folder can
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
/// reads rows' <see cref="ICleanupProvider.ToolRoots"/>, and a row may build those from its own
/// walk, so a question asked from inside either would ask a row for the declaration it is still
/// building. <see cref="UnofferedAsync"/> is the one place a plan's spared children meet it.</para>
///
/// <para><b>Each question reads only the rows that could declare its folder.</b> A row states
/// where its roots can lie without touching the disk (<see cref="ICleanupProvider.ToolRootScope"/>),
/// and a question about a folder reads only the rows whose scope covers it. The Gradle row asking
/// about its own home never waits for the Claude Code conversations row to date its folders or for
/// the Chromium rows to walk both application-data roots, which together are most of the reading
/// and can take seconds on a busy drive.</para>
///
/// <para><b>Asked from several plans at once, and awaited.</b> Each row is read once per pass, for
/// whichever question needed it first, and every question that needs it meanwhile awaits that one
/// reading rather than starting a second. A reading runs its row on a thread of the walk's own
/// (<see cref="DeclaredRoots"/>), so no plan holds a pool thread for it, and no more rows are read at
/// once than a pass plans.</para>
/// </summary>
public sealed class RowDeclarations
{
    private readonly PassMemo<Readings> _readings = new();
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
        _readings.Forget();
    }

    /// <summary>
    /// Drop every reading. The rows rebuild their declarations for each planning pass, so a reading
    /// kept across passes would miss a folder that appeared between them, and a scope read from the
    /// environment may have moved with a variable.
    /// </summary>
    public void Invalidate() => _readings.Forget();

    /// <summary>
    /// The children in <paramref name="spared"/> that no row declares it removes: what a plan asserts
    /// survived and counts as left alone. Each is asked about as a directory, so a file spared beside
    /// them is never excused by a row's offer, and stays asserted.
    ///
    /// <para>The only way a plan asks, so the sweep rule on awaiting it holds for every row.</para>
    ///
    /// <para><b>Started where the children are known, and awaited where the plan reads them.</b> The
    /// first question about a folder in a pass reads every row whose scope covers it, and a row that
    /// awaited that before measuring its own targets would hold its planning slot doing nothing. The
    /// measurement needs only the targets, so it runs while this is answered.
    /// <c>CodeRuleSweepTests</c> refuses an <c>await</c> written on the question itself.</para>
    /// </summary>
    /// <param name="ct">
    /// Checked before each row is read. A row's declaration cannot be stopped part-way.
    /// </param>
    public async Task<IReadOnlyList<(string Path, string Reason)>> UnofferedAsync(
        IReadOnlyList<(string Path, string Reason)> spared,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spared);

        // A row whose declaration asked this would wait for the reading it is part of, which never
        // ends and says nothing about why.
        if (DeclaredRoots.IsBeingRead)
        {
            throw new InvalidOperationException(
                "A row asked what other rows offer while its own declaration was being read.");
        }

        var readings = _readings.Get(() => new Readings(Volatile.Read(ref _rows)));
        List<(string Path, string Reason)> unoffered = [];

        // A plan's spared children are nearly always children of one folder, so the rows covering a
        // folder are found and read once for the run of children that share it.
        string? folder = null;
        Dictionary<string, List<ToolRoot>>[] declared = [];

        foreach (var child in spared)
        {
            if (Path.GetDirectoryName(child.Path) is not { } parent || LongPath.Entry(parent) is not { } key)
            {
                unoffered.Add(child);
                continue;
            }

            if (!key.Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                declared = await readings.CoveringAsync(key, ct).ConfigureAwait(false);
                folder = key;
            }

            if (!IsOffered(declared, key, Path.GetFileName(child.Path)))
            {
                unoffered.Add(child);
            }
        }

        return unoffered;
    }

    private static bool IsOffered(Dictionary<string, List<ToolRoot>>[] declared, string folder, string name)
    {
        var child = new ToolRootChild(name, ChildKind.Folder);

        foreach (var byFolder in declared)
        {
            if (byFolder.TryGetValue(folder, out var roots) && roots.Exists(root => root.Recognises(child)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One pass's readings: each row's scope, read once, and its declaration, read the first time a
    /// question covers it (G4).
    /// </summary>
    private sealed class Readings
    {
        private readonly Row[] _rows;

        // No more rows read at once than a pass plans: each read holds a thread of its own for as long
        // as its row walks. Never disposed, since a question
        // cancelled while queued here may still be waiting when the pass is forgotten.
        private readonly SemaphoreSlim _atOnce = new(CleanupPlanner.ProvidersAtOnce);

        public Readings(IReadOnlyList<ICleanupProvider> rows) =>
            _rows = [.. rows.Select(row => new Row(row, row.ToolRootScope)).Where(row => !row.Scope.IsEmpty)];

        /// <summary>The declarations, each indexed by folder, of every row whose scope covers <paramref name="folder"/>.</summary>
        public async Task<Dictionary<string, List<ToolRoot>>[]> CoveringAsync(string folder, CancellationToken ct)
        {
            List<Task<Dictionary<string, List<ToolRoot>>>> reading = [];

            // Every covering row is started before any is awaited, so they are read side by side.
            foreach (var row in _rows)
            {
                if (row.Scope.Covers(folder))
                {
                    reading.Add(DeclaredAsync(row, ct));
                }
            }

            return await Task.WhenAll(reading).ConfigureAwait(false);
        }

        private async Task<Dictionary<string, List<ToolRoot>>> DeclaredAsync(Row row, CancellationToken ct)
        {
            while (true)
            {
                var reading = row.Declared.Get(() => ReadAsync(row.Provider, ct));

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

        private async Task<Dictionary<string, List<ToolRoot>>> ReadAsync(ICleanupProvider row, CancellationToken ct)
        {
            await _atOnce.WaitAsync(ct).ConfigureAwait(false);
            IReadOnlyList<ToolRoot> roots;

            try
            {
                // DeclaredRoots checks the token again before the row is read, so a slot freed while
                // the pass is being cancelled starts no row.
                roots = await DeclaredRoots.ReadAsync(row, ct).ConfigureAwait(false);
            }
            finally
            {
                _atOnce.Release();
            }

            var byFolder = new Dictionary<string, List<ToolRoot>>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
            {
                if (LongPath.Configured(root.Path) is not { } key)
                {
                    continue;
                }

                if (!byFolder.TryGetValue(key, out var atFolder))
                {
                    byFolder[key] = atFolder = [];
                }

                atFolder.Add(root);
            }

            return byFolder;
        }
    }

    /// <summary>A row with a scope, and its declaration once a question has read it.</summary>
    private sealed record Row(ICleanupProvider Provider, ToolRootScope Scope)
    {
        public PassMemo<Task<Dictionary<string, List<ToolRoot>>>> Declared { get; } = new();
    }
}
