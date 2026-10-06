using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Explore page's Files layout: the largest files at any depth under the folder on screen, the
/// four filters over them, and the sentence saying how many there are.
///
/// <para>Separate from <see cref="ExploreViewModel"/>, as <see cref="ExploreGrowth"/> is, because it
/// holds state of its own that the page only reads: the filters, the last answer and the rows. The
/// decisions in it are Core's — what is listed and in what order is <see cref="LargestFiles"/>',
/// and what may be removed is <see cref="ExploreActions"/>' — and this decides only when to ask.</para>
///
/// <para><b>Ordered by size, never by removability, and nothing in it selects anything (§7.1).</b>
/// The page's one selection is <see cref="ExploreSelection"/>'s, and a row here enters it only by a
/// gesture on that row, so a filter can narrow what is listed and never what is acted on.</para>
/// </summary>
public sealed partial class ExploreFiles : ObservableObject
{
    /// <summary>The minimum sizes offered, in the binary units every size on the page is stated in.</summary>
    private static readonly long[] Minimums = [0, 10L << 20, 100L << 20, 1L << 30, 10L << 30];

    /// <summary>The three boxes' entries, built once for the life of the app (G5).</summary>
    private static readonly IReadOnlyList<string> Types = ["Any type", .. FileCategories.All.Select(FileCategories.Label)];

    private static readonly IReadOnlyList<string> MinimumSizes =
        ["Any size", "10 MB or more", "100 MB or more", "1 GB or more", "10 GB or more"];

    private static readonly IReadOnlyList<string> Ages = [.. FileAges.All.Select(FileAges.Label)];

    private readonly ExploreActions _actions;
    private readonly Func<int, bool> _wasRemoved;
    private readonly TimeProvider _time;

    private ExploreTree? _tree;
    private int _root;

    /// <summary>
    /// The instant every age on <see cref="_tree"/> is measured from, read once per tree. Fixed for the
    /// tree's life, so an age filter answers the same way on every keystroke and an earlier answer can
    /// be reused for it (see <see cref="LargestFiles.Find"/>).
    /// </summary>
    private DateTime _now;

    /// <summary>The last answer, which the next question is compared with. About <see cref="_tree"/> only.</summary>
    private FileRanking? _ranking;

    /// <summary>
    /// Every row built for <see cref="_tree"/> under the policy there is now, by node, so a filter
    /// typed a letter at a time asks the policy about each file once rather than once per keystroke.
    /// A row's refusal is read from the machine, where each of its paths is mounted, and that is kept
    /// for the operation's life (G4).
    ///
    /// <para>Replaced rather than cleared when the tree or the policy changes, so a search still
    /// running for the old one fills a dictionary nothing reads. Concurrent because the search that
    /// fills it runs off the window's thread, and one being cancelled can overlap the next.</para>
    /// </summary>
    private ConcurrentDictionary<int, ExploreFileRow> _built = new();

    /// <summary>The search in flight, cancelled when another replaces it.</summary>
    private CancellationTokenSource? _finding;

    /// <summary>
    /// Which search is the latest. A search that finishes after another has started is about a
    /// question nobody is asking any more, and its answer is dropped.
    /// </summary>
    private int _generation;

    /// <param name="wasRemoved">
    /// Whether a node of the tree on screen has gone since the scan. See
    /// <see cref="ExploreSelection.WasRemoved"/>: a removed file is no longer listed, because nothing
    /// may offer it.
    /// </param>
    /// <param name="time">Where the instant ages are measured from is read.</param>
    public ExploreFiles(ExploreActions actions, Func<int, bool> wasRemoved, TimeProvider time)
    {
        _actions = actions;
        _wasRemoved = wasRemoved;
        _time = time;

        // A refusal on a row is the one thing here whose answer can arrive late: until the policy is
        // built every path is refused with a sentence saying why. Asked again once it is, so the rows
        // built against the last policy are let go.
        _actions.Ready += (_, _) =>
        {
            _built = new();
            Find();
        };
    }

    /// <summary>
    /// The files, largest first. Updated in place rather than rebuilt, so the list keeps its scroll
    /// position and its selection while a filter is typed (G4).
    /// </summary>
    public ObservableCollection<ExploreFileRow> Rows { get; } = [];

    /// <summary>The types the type box offers, "any type" first.</summary>
    public IReadOnlyList<string> TypeLabels => Types;

    /// <summary>The minimum sizes the size box offers. See <see cref="Minimums"/>.</summary>
    public IReadOnlyList<string> MinimumLabels => MinimumSizes;

    /// <summary>The ages the age box offers.</summary>
    public IReadOnlyList<string> AgeLabels => Ages;

    /// <summary>Text a file's name contains, or a wildcard it matches. See <see cref="FileFilter.Name"/>.</summary>
    [ObservableProperty]
    public partial string NameFilter { get; set; } = string.Empty;

    /// <summary>Which of <see cref="TypeLabels"/> is chosen.</summary>
    [ObservableProperty]
    public partial int TypeIndex { get; set; }

    /// <summary>Which of <see cref="MinimumLabels"/> is chosen.</summary>
    [ObservableProperty]
    public partial int MinimumIndex { get; set; }

    /// <summary>Which of <see cref="AgeLabels"/> is chosen.</summary>
    [ObservableProperty]
    public partial int AgeIndex { get; set; }

    /// <summary>How many files are listed and how many matched, or empty while nothing is.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>
    /// Whether the layout is on screen. Nothing is searched while it is not: the pass is over every
    /// file below the folder on screen, and a walked scan publishes a tree every few hundred
    /// milliseconds.
    /// </summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>
    /// Whether <see cref="Rows"/> is being rewritten from here. Read by the page for the reason
    /// <see cref="ExploreViewModel.IsShowingRows"/> gives.
    /// </summary>
    public bool IsShowingRows { get; private set; }

    /// <summary>
    /// The filter the four boxes describe, with an index no box offers read as that box's first
    /// entry: a combo box reports -1 while it has no selection.
    /// </summary>
    private FileFilter Filter => new(
        NameFilter,
        TypeIndex > 0 && TypeIndex <= FileCategories.All.Count ? FileCategories.All[TypeIndex - 1] : null,
        MinimumIndex > 0 && MinimumIndex < Minimums.Length ? Minimums[MinimumIndex] : 0,
        AgeIndex > 0 && AgeIndex < FileAges.All.Count ? FileAges.All[AgeIndex] : FileAge.Any);

    /// <summary>Raised once <see cref="Rows"/> has changed, so the page puts the list's highlight back.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Point the layout at <paramref name="root"/> of <paramref name="tree"/>, or at nothing. Called on
    /// every redraw of the page, and on a removal, and searches only while the layout is on screen.
    /// </summary>
    public void Show(ExploreTree? tree, int root)
    {
        if (!ReferenceEquals(tree, _tree))
        {
            _now = _time.GetUtcNow().UtcDateTime;

            // An answer holds its tree, and a tree is a volume's worth of arrays.
            _ranking = null;
            _built = new();

            Carry(tree);
        }

        _tree = tree;
        _root = root;

        Find();
    }

    /// <summary>
    /// Keep only the rows that still name what they named, now <paramref name="arriving"/> is the
    /// tree on screen, and do it before anything can be clicked.
    ///
    /// <para><b>§7.1, and the reason this cannot wait for the search.</b> A row holds a node number,
    /// and a click sends that number to <see cref="ExploreSelection"/>, which reads it against the tree
    /// on screen. The search for the arriving tree takes as long as a pass over it, and a row left
    /// standing from the last tree for that long would select, and offer to delete, whatever its
    /// number means in this one: another file, or a number past the end that throws. So a row stays
    /// only where the arriving tree puts its number at the same path, which is
    /// <see cref="ExplorePlace.TryCarry"/>'s rule and the selection's own. The snapshots of one walk
    /// keep their rows, and a scan of anything else keeps none.</para>
    ///
    /// <para>Off screen nothing is kept, because nothing is looking at the rows.</para>
    /// </summary>
    private void Carry(ExploreTree? arriving)
    {
        ExploreFileRow[] kept = IsActive && arriving is not null
            ? [.. Rows.Where(row => string.Equals(arriving.TryPathOf(row.Node), row.Path, StringComparison.OrdinalIgnoreCase))]
            : [];

        if (kept.Length == Rows.Count)
        {
            return;
        }

        ShowRows(kept);

        // The sentence counted what is no longer listed, and says nothing until the search answers.
        Summary = string.Empty;
    }

    /// <summary>
    /// List only the files of <paramref name="category"/>, from a kind chosen in the breakdown of the
    /// folder on screen. The other filters stay as the reader set them, because they are on screen
    /// above the list and say what it is narrowed by.
    /// </summary>
    public void ListOnly(FileCategory category)
    {
        // The inverse of Filter's reading of the box, which offers "any type" first.
        for (var at = 0; at < FileCategories.All.Count; at++)
        {
            if (FileCategories.All[at] == category)
            {
                TypeIndex = at + 1;
                return;
            }
        }
    }

    partial void OnNameFilterChanged(string value) => Find();

    partial void OnTypeIndexChanged(int value) => Find();

    partial void OnMinimumIndexChanged(int value) => Find();

    partial void OnAgeIndexChanged(int value) => Find();

    partial void OnIsActiveChanged(bool value) => Find();

    /// <summary>
    /// Ask again for the tree, the folder and the filter there are now, off the window's thread.
    /// Fire and forget, because the latest question is the only one whose answer is shown.
    /// </summary>
    private void Find() => _ = FindAsync();

    private async Task FindAsync()
    {
        var generation = ++_generation;

        _finding?.Cancel();
        _finding = null;

        if (!IsActive)
        {
            return;
        }

        if (_tree is not { } tree)
        {
            ShowRows([]);
            Summary = string.Empty;
            return;
        }

        var finding = new CancellationTokenSource();
        _finding = finding;

        var (root, filter, now, previous, built) = (_root, Filter, _now, _ranking, _built);

        try
        {
            var (ranking, rows) = await Task.Run(
                () =>
                {
                    var found = LargestFiles.Find(tree, root, filter, now, LargestFiles.Limit, previous, finding.Token);
                    var listed = new ExploreFileRow[found.Files.Count];

                    for (var at = 0; at < listed.Length; at++)
                    {
                        finding.Token.ThrowIfCancellationRequested();

                        listed[at] = built.GetOrAdd(
                            found.Files[at], node => ExploreFileRow.For(tree, node, now, _actions.Verdict));
                    }

                    return (found, listed);
                },
                finding.Token);

            if (generation != _generation)
            {
                return;
            }

            _ranking = ranking;

            var gone = ShowRows(rows);
            Summary = SummaryOf(ranking, gone);
        }
        catch (OperationCanceledException)
        {
            // Another question replaced this one, and its answer is the one that will be shown.
        }
    }

    /// <summary>
    /// Bring <see cref="Rows"/> to <paramref name="arriving"/>, less anything removed since the scan.
    /// </summary>
    /// <returns>How many of <paramref name="arriving"/> were left out for having been removed.</returns>
    private int ShowRows(IReadOnlyList<ExploreFileRow> arriving)
    {
        ExploreFileRow[] kept = [.. arriving.Where(row => !_wasRemoved(row.Node))];

        IsShowingRows = true;

        try
        {
            LiveList.Show(Rows, kept, row => row.Key);
        }
        finally
        {
            IsShowingRows = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return arriving.Count - kept.Length;
    }

    /// <summary>
    /// How many files are listed and of how many. The count is of what matched, so a reader whose
    /// filter matches fifty thousand files is told the list is the top of it rather than all of it.
    /// </summary>
    /// <param name="gone">
    /// How many listed files were removed since the scan. They are taken off the counts as they are
    /// off the rows, so the sentence describes the list under it. A file removed from outside the
    /// list is still counted, as the sizes on the page still count it, and the stale note says so.
    /// </param>
    private static string SummaryOf(FileRanking ranking, int gone)
    {
        var (matched, listed) = (ranking.Matched - gone, ranking.Files.Count - gone);

        return (matched, ranking.IsComplete, ranking.Filter.IsEverything) switch
        {
            (0, _, true) => "There are no files here.",
            (0, _, false) => "No file here matches.",
            (1, _, true) => "1 file here.",
            (1, _, false) => "1 file matches.",
            (var count, true, true) => $"{count:N0} files here, largest first.",
            (var count, true, false) => $"{count:N0} files match, largest first.",
            (var count, false, true) => $"The {listed:N0} largest of {count:N0} files here.",
            (var count, false, false) => $"The {listed:N0} largest of {count:N0} files that match.",
        };
    }
}
