using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Scanning;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Duplicates page (§7.4): the locations (<see cref="Locations"/>), the criteria and filters
/// (<see cref="Filters"/>), the search with its stages and its stop, the groups as they arrive, the
/// notes on what the search did not look at, and the marking and removing that work on its groups
/// (<see cref="Marking"/>). Every decision is Core's: what a search is
/// (<see cref="Core.Configuration.DuplicatePreferences"/>), where each group goes
/// (<see cref="DuplicateMarks.Add"/>) and what the notes say (<see cref="DuplicateSearchNotes"/>).
/// </summary>
public sealed partial class DuplicatesViewModel : ObservableObject
{
    private const string SpaceSentence =
        "Sizes are what the copies occupy on disk, and removing them may free less: copies on a Dev Drive or a "
        + "deduplicated volume can share their space, and the Recycle Bin frees nothing until it is emptied.";

    private readonly RunDuplicateSearch _run;
    private readonly Func<ElevationRequest, bool> _relaunch;
    private readonly RunningActions _running;

    /// <summary>The search running now, or null, so a callback from one that was replaced is ignored.</summary>
    private Search? _search;

    /// <param name="actions">Runs the rules and the removal over what a search found.</param>
    /// <param name="requested">The locations an elevated relaunch was asked to search, or null for an ordinary launch.</param>
    public DuplicatesViewModel(
        RunDuplicateSearch run,
        DuplicateActions actions,
        PreferenceService preferences,
        DriveList drives,
        bool isElevated,
        Func<ElevationRequest, bool> relaunch,
        RunningActions running,
        DuplicatesRequest? requested = null)
    {
        _run = run;
        _relaunch = relaunch;
        _running = running;
        Filters = new DuplicateFiltersViewModel(preferences);
        Filters.Changed += (_, _) => Judge();
        Marking = new DuplicateMarkingViewModel(actions, Groups);

        // A new search would take the groups from under a rule or a removal reading them.
        Marking.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(DuplicateMarkingViewModel.IsBusy))
            {
                SearchCommand.NotifyCanExecuteChanged();
            }
        };
        Locations = new DuplicateLocationsViewModel(drives, requested?.Locations ?? []);
        Locations.Changed += (_, _) =>
        {
            Judge();
            Marking.LocationsChanged(Locations.Chosen);
        };
        CanElevate = ElevationOffer.ShouldOffer(isElevated);
        IsRequested = requested is not null;

        // A clean or a removal on another page ends with this process, so elevating waits for it.
        _running.Changed += (_, _) => ElevateCommand.NotifyCanExecuteChanged();

        Judge();
    }

    public DuplicateFiltersViewModel Filters { get; }

    public DuplicateLocationsViewModel Locations { get; }

    public DuplicateMarkingViewModel Marking { get; }

    /// <summary>Why the search cannot run as it stands, or an empty string where it can.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyPropertyChangedFor(nameof(SearchStatus))]
    public partial string WhyCannotSearch { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(SearchStatus))]
    public partial bool IsSearching { get; private set; }

    /// <summary>The stage the search has reached, in words.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchStatus))]
    public partial string Stage { get; private set; } = string.Empty;

    /// <summary>The line beside the Search button: the stage while a search runs, and otherwise why it cannot.</summary>
    public string SearchStatus => IsSearching ? Stage : WhyCannotSearch;

    /// <summary>How far the stage has got, from 0 to 100, where it can say.</summary>
    [ObservableProperty]
    public partial double StageProgress { get; private set; }

    /// <summary>Whether the stage cannot say how far it has got, so the bar shows only that it is moving.</summary>
    [ObservableProperty]
    public partial bool StageProgressIsUnknown { get; private set; } = true;

    [ObservableProperty]
    public partial string Headline { get; private set; } =
        "Choose the drives and folders to search, and what two files must share to be a match.";

    /// <summary>What the space figures are, and that a removal may free less (§7.4).</summary>
    public string SpaceNote => SpaceSentence;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ElevateCommand))]
    public partial bool CanElevate { get; private set; }

    /// <summary>Whether this instance was opened to search locations an earlier one handed over.</summary>
    public bool IsRequested { get; }

    /// <summary>
    /// The groups, the one that could free the most first, each placed as it arrives and never moved,
    /// so the reader's place in the list holds while a search runs.
    /// </summary>
    public ObservableCollection<DuplicateGroupRow> Groups { get; } = [];

    /// <summary>What the search did not look at, or left out.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    public partial string NotesHeading { get; private set; } = string.Empty;

    public bool HasNotes => NotesHeading.Length > 0;

    /// <summary>The marks for the last search's groups, which marking and removing work on.</summary>
    [ObservableProperty]
    public partial DuplicateMarks? Marks { get; private set; }

    /// <summary>Raised when an elevated replacement has started, so the page can close this one.</summary>
    public event EventHandler? ReplacedByElevatedInstance;

    private bool CanSearch() => !IsSearching && !Marking.IsBusy && WhyCannotSearch.Length == 0;

    /// <summary>
    /// Search the locations with the stored criteria and filters, replacing what the last search
    /// found.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        var search = new Search(Filters.Current.Search(Locations.Chosen));
        _search = search;

        // The list is about another search now, whose groups share nothing with these, so one reset
        // costs less than a removal for each row and there is no place in it worth keeping.
        Groups.Clear();
        Notes.Clear();
        NotesHeading = string.Empty;
        Marks = null;
        Marking.Started(search.Asked);
        IsSearching = true;
        Headline = "Searching…";
        Show(new DuplicateSearchProgress(DuplicateSearchStage.Finding, 0, Total: null));

        try
        {
            var result = await _run(
                search.Asked,
                marks => search.Marks = marks,
                new Progress<CandidateFinding>(finding => Found(search, finding)),
                new Progress<DuplicateGroup>(group => Confirmed(search, group)),
                new Progress<DuplicateSearchProgress>(progress => Reached(search, progress)),
                search.Stop.Token);

            if (ReferenceEquals(_search, search))
            {
                Finished(search, result);
            }
        }
        catch (OperationCanceledException) when (search.Stop.IsCancellationRequested)
        {
            if (ReferenceEquals(_search, search))
            {
                Headline = "Stopped before the files to compare were all found, so nothing was confirmed.";
            }
        }
        finally
        {
            if (ReferenceEquals(_search, search))
            {
                _search = null;
                Marking.Ended();
                IsSearching = false;
                Stage = string.Empty;
            }

            search.Stop.Dispose();
        }
    }

    private bool CanStop() => IsSearching;

    /// <summary>Stop the search, keeping every group it has confirmed.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _search?.Stop.Cancel();

    /// <summary>Elevating ends this process, so it waits for every action on every page.</summary>
    private bool CanElevateNow() => CanElevate && _running.MayEndProcess;

    [RelayCommand(CanExecute = nameof(CanElevateNow))]
    private void Elevate()
    {
        if (_relaunch(new DuplicatesRequest(Locations.Chosen)))
        {
            ReplacedByElevatedInstance?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Judge() => WhyCannotSearch = Filters.Current.WhyRefused(Locations.Chosen) ?? string.Empty;

    private void Found(Search search, CandidateFinding finding)
    {
        if (!ReferenceEquals(_search, search))
        {
            return;
        }

        Marks = search.MarksMade;
        Marking.Made(Marks);
        ShowNotes(DuplicateSearchNotes.Of(finding));
    }

    /// <summary>
    /// Place a confirmed group where the marks put it, by the space it could free. One insertion, and
    /// no row already there moves, so a reader part-way down the list stays where they are.
    /// </summary>
    private void Confirmed(Search search, DuplicateGroup group)
    {
        if (!ReferenceEquals(_search, search))
        {
            return;
        }

        // The marks are handed over before the first group, by the search's own thread, and are read
        // here rather than from Marks so a group never waits on the finding's notes being shown.
        var marks = search.MarksMade;
        Marking.Made(marks);
        var (added, index) = marks.Add(group);
        Groups.Insert(index, new DuplicateGroupRow(added, marks.Keeping, Marking.Toggle, Marking.MayMark));
        Headline = $"Searching… {Groups.Count:N0} {(Groups.Count == 1 ? "group" : "groups")} so far.";
    }

    private void Reached(Search search, DuplicateSearchProgress progress)
    {
        if (ReferenceEquals(_search, search))
        {
            Show(progress);
        }
    }

    private void Show(DuplicateSearchProgress progress)
    {
        (Stage, var fraction) = progress switch
        {
            { Stage: DuplicateSearchStage.Finding, Scan: { } scan } =>
                ($"Reading the locations: {scan.Done:N0} entries so far", scan.Fraction),
            { Stage: DuplicateSearchStage.Finding } => ("Reading the locations", null),
            { Stage: DuplicateSearchStage.FirstAndLastBlocks } =>
                ($"Comparing the start and end of each file: {progress.Done:N0} of {progress.Total:N0}", Fraction(progress)),
            _ => ($"Reading the files that still match in full: {progress.Done:N0} of {progress.Total:N0}", Fraction(progress)),
        };

        StageProgressIsUnknown = fraction is null;
        StageProgress = (fraction ?? 0) * 100;
    }

    private static double? Fraction(DuplicateSearchProgress progress) =>
        progress.Total is > 0 and var total ? Math.Clamp((double)progress.Done / total, 0, 1) : null;

    private void Finished(Search search, DuplicateSearchResult result)
    {
        var marks = search.MarksMade;
        Marks = marks;
        ShowNotes(DuplicateSearchNotes.Of(result));

        var freeable = Groups.Sum(row => row.Marks.FreeableSpace(marks.Keeping));
        var found = Groups.Count == 0
            ? "No duplicates found."
            : $"{Groups.Count:N0} {(Groups.Count == 1 ? "group" : "groups")}. Copies that could go occupy {FreeSpace.Format(freeable)}.";

        Headline = result.Stopped ? "Stopped. " + found : found;
    }

    /// <summary>
    /// Bring the notes up to date in place: the finding's notes stay where they are, and what the
    /// end of the search adds (files left out while reading, a stop) is written in after them.
    /// </summary>
    private void ShowNotes(IReadOnlyList<string> notes)
    {
        Core.Viewing.LiveList.Rewrite(Notes, notes);
        NotesHeading = notes.Count switch
        {
            0 => string.Empty,
            1 => "1 note on what the search did not look at",
            _ => $"{notes.Count:N0} notes on what the search did not look at",
        };
    }

    /// <summary>One search's own state, so a replaced search's late callbacks touch nothing.</summary>
    private sealed class Search(DuplicateSearch asked)
    {
        public DuplicateSearch Asked { get; } = asked;

        public CancellationTokenSource Stop { get; } = new();

        /// <summary>Written once by the search's thread before the first group is confirmed, and read on the page's.</summary>
        public DuplicateMarks? Marks
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        /// <summary>
        /// The marks, which the search hands over before its finding, its first group and its end.
        /// Anything arriving without them is a broken contract, not a search to show half of: a group
        /// left out of the list would read as files with no duplicate.
        /// </summary>
        public DuplicateMarks MarksMade =>
            Marks ?? throw new InvalidOperationException("The search reported before handing over its marks.");
    }
}
