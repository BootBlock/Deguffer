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
/// building. <see cref="LevelWalk.Survivors"/> is the one place a walk's children meet it.</para>
/// </summary>
public sealed class RowDeclarations
{
    private IReadOnlyList<ICleanupProvider> _rows = [];
    private Dictionary<string, List<ToolRoot>>? _byFolder;
    private bool _building;

    /// <summary>
    /// The rows whose declarations are consulted. <see cref="Execution.CleanupPlanner"/> admits its
    /// own, so this always names every row in the planner. Until then it names none, which excuses
    /// nothing: a row built alone asserts every child it spares.
    /// </summary>
    public void Admit(IReadOnlyList<ICleanupProvider> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        _rows = rows;
        _byFolder = null;
    }

    /// <summary>
    /// Drop the index. The rows rebuild their declarations for each planning pass, so an index kept
    /// across passes would miss a folder that appeared between them.
    /// </summary>
    public void Invalidate() => _byFolder = null;

    /// <summary>
    /// Whether some row declares <paramref name="folder"/>, a directory that is not a link, as one it
    /// removes from the directory it sits in.
    /// </summary>
    /// <param name="ct">
    /// Checked between rows while the set is read. A row's declaration cannot be stopped part-way, but
    /// the first question in a pass reads every row's, inside the plan of whichever row asked.
    /// </param>
    public bool OfferedByARow(string folder, CancellationToken ct = default) =>
        Path.GetDirectoryName(folder) is { } parent
        && LongPath.Entry(parent) is { } key
        && ByFolder(ct).TryGetValue(key, out var roots)
        && roots.Exists(root => root.Recognises(new ToolRootChild(Path.GetFileName(folder), ChildKind.Folder)));

    /// <summary>Built once per planning pass (G4), because a plan asks once per spared child.</summary>
    private Dictionary<string, List<ToolRoot>> ByFolder(CancellationToken ct)
    {
        if (_byFolder is { } built)
        {
            return built;
        }

        // A row whose declaration asked this would recurse until the stack ran out, which ends the
        // process with nothing to say why.
        if (_building)
        {
            throw new InvalidOperationException(
                "A row asked what other rows offer while its own declaration was being read.");
        }

        _building = true;

        try
        {
            built = new Dictionary<string, List<ToolRoot>>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in _rows)
            {
                ct.ThrowIfCancellationRequested();

                foreach (var root in row.ToolRoots)
                {
                    if (LongPath.Configured(root.Path) is not { } key)
                    {
                        continue;
                    }

                    if (!built.TryGetValue(key, out var roots))
                    {
                        built[key] = roots = [];
                    }

                    roots.Add(root);
                }
            }

            return _byFolder = built;
        }
        finally
        {
            _building = false;
        }
    }
}
