using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Scanning;
using Deguffer.Core.Working;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Duplicates page's marking and removing half (§7.4): marks by hand and by rule, the
/// confirmation, the removal and what it did. What may be marked, what a rule marks, what the
/// confirmation says and what goes are Core's (<see cref="GroupMarks"/>, <see cref="DuplicateMarks"/>,
/// <see cref="RemovalConfirmation"/>, <see cref="DuplicateActions"/>); this says only when the page
/// lets them run.
///
/// <para><b>When marks may change.</b> Only while the groups shown answer for the locations chosen
/// now: a location added, taken away or given another role after the search leaves them in the old
/// roles (<see cref="DuplicateSearch.WhyResultsDoNotApply"/>). Not after a removal, whose groups
/// describe the disk as it was before it. Not while a rule, a confirmation or a removal reads the
/// marks on another thread.</para>
///
/// <para><b>When a rule or a removal may run.</b> When marks may change, and once the search has
/// ended, because the search adds its groups on this thread while a rule and a removal read them on
/// another. Core refuses either before then as well (<see cref="DuplicateMarks.Complete"/>).</para>
/// </summary>
public sealed partial class DuplicateMarkingViewModel : ObservableObject
{
    private const string StillSearching = "Rules and removal wait for the search to finish.";

    private const string Spent =
        "These groups describe the disk as it was before the removal. Search again to mark and remove more.";

    private const string Busy = "Wait for the rule or the removal to finish.";

    private readonly DuplicateActions _actions;
    private readonly IReadOnlyList<DuplicateGroupRow> _groups;
    private readonly BusyWork _work;

    private DuplicateMarks? _marks;
    private DuplicateSearch? _asked;
    private string? _stale;
    private bool _mayMark;
    private CancellationTokenSource? _stopping;

    /// <param name="groups">The page's groups, in the order it shows them.</param>
    public DuplicateMarkingViewModel(DuplicateActions actions, IReadOnlyList<DuplicateGroupRow> groups, BusyWork work)
    {
        _actions = actions;
        _groups = groups;
        _work = work;
    }

    /// <summary>The named rules, in the order the rule list offers them (§7.4).</summary>
    public static IReadOnlyList<string> RuleNames { get; } =
    [
        "Keep the newest copy",
        "Keep the oldest copy",
        "Keep the copy with the shortest path",
        "Keep the copies in a folder",
        "Mark every copy in a folder",
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuleNeedsFolder))]
    [NotifyCanExecuteChangedFor(nameof(RunRuleCommand))]
    public partial int RuleIndex { get; set; }

    /// <summary>The folder a folder rule is about, as the folder picker returned it, or an empty string.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunRuleCommand))]
    public partial string RuleFolder { get; set; } = string.Empty;

    public bool RuleNeedsFolder => RuleAsksForFolder(RuleIndex);

    /// <summary>Whether a rule, by its place in <see cref="RuleNames"/>, is about a folder the user chooses.</summary>
    public static bool RuleAsksForFolder(int index) => index >= 3;

    /// <summary>Whether a rule, a confirmation or a removal is running, so the page waits for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Whether nothing is running, so the locations may change.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>
    /// What the last rule or removal did, or nothing, with the §5.6 checks a removal could not pass
    /// beside it.
    /// </summary>
    public StatusLineViewModel Outcome { get; } = new(StatusLine.Empty);

    /// <summary>Why nothing can be marked, ruled or removed now, or an empty string where something can.</summary>
    public string WhyClosed => WhyCannotAct ?? _marks?.WhyRulesCannotMark ?? string.Empty;

    public bool HasWhyClosed => WhyClosed.Length > 0;

    /// <summary>Whether a rule may mark now, given a folder where it asks for one, which the page's rule menu asks.</summary>
    public bool CanMarkByRule => WhyCannotAct is null && _marks!.WhyRulesCannotMark is null && _groups.Count > 0;

    /// <summary>
    /// How many copies are marked, and what the marks that stand occupy, which a removal may free less
    /// than, or "None" where nothing is marked.
    /// </summary>
    public string MarkedFigure => Marked switch
    {
        0 => "None",
        1 => $"1 copy, {FreeSpace.Format(MarkedSpace)}",
        var count => $"{count:N0} copies, {FreeSpace.Format(MarkedSpace)}",
    };

    private long MarkedSpace => _groups.Sum(group => group.MarkedSpace);

    private int Marked => _groups.Sum(group => group.Marks.MarkedCount);

    /// <summary>Why no mark may change now, or null where marks may change.</summary>
    private string? WhyCannotMark =>
        _marks is not { } marks ? string.Empty : _stale ?? (marks.WasRemovedFrom ? Spent : null) ?? (IsBusy ? Busy : null);

    /// <summary>Why no rule or removal may start now, or null where one may.</summary>
    private string? WhyCannotAct => WhyCannotMark ?? (_marks!.IsComplete ? null : StillSearching);

    /// <summary>A search began, for <paramref name="asked"/>: what the last one found is gone.</summary>
    internal void Started(DuplicateSearch asked)
    {
        _marks = null;
        _asked = asked;
        _stale = null;
        Outcome.Say(StatusLine.Empty);

        // Counted as well as refreshed: the groups the count was of are gone.
        Counted();
    }

    /// <summary>The search handed over the marks it adds its groups to, which may be marked by hand from now on.</summary>
    internal void Made(DuplicateMarks marks)
    {
        if (!ReferenceEquals(_marks, marks))
        {
            _marks = marks;
            Refresh();
        }
    }

    /// <summary>The search ended, stopped or not, and added its last group.</summary>
    internal void Ended()
    {
        _marks?.Complete();
        Refresh();
    }

    /// <summary>The locations chosen changed: what the search found holds only while they are the ones it searched.</summary>
    internal void LocationsChanged(IReadOnlyCollection<SearchLocation> chosen)
    {
        _stale = _asked?.WhyResultsDoNotApply(chosen);
        Refresh();
    }

    /// <summary>Mark or unmark one copy, as Core allows, answering why not.</summary>
    internal string? Toggle(DuplicateCopyRow row)
    {
        if (_marks is not { } marks || WhyCannotMark is not null)
        {
            return WhyCannotMark;
        }

        string? refused = null;

        if (row.IsMarked)
        {
            row.Group.Unmark(row.Copy);
        }
        else
        {
            refused = row.Group.Mark(row.Copy, marks.Keeping);
        }

        row.Owner.Recount(marks.Keeping);
        Counted();

        return refused;
    }

    /// <summary>Whether marks may change now, which each copy's check box asks.</summary>
    internal bool MayMark() => _mayMark;

    private bool CanRunRule() => CanMarkByRule && (!RuleNeedsFolder || RuleFolder.Length > 0);

    [RelayCommand(CanExecute = nameof(CanRunRule))]
    private async Task RunRuleAsync()
    {
        // Asked again, because a command can be invoked without asking whether it may run.
        if (!CanRunRule())
        {
            return;
        }

        var marks = _marks!;
        MarkingRule rule = RuleIndex switch
        {
            0 => new MarkingRule.KeepNewest(),
            1 => new MarkingRule.KeepOldest(),
            2 => new MarkingRule.KeepShortestPath(),
            3 => new MarkingRule.KeepInFolder(RuleFolder),
            _ => new MarkingRule.MarkInFolder(RuleFolder),
        };

        await StoppableAsync(async (stop, _) =>
        {
            RuleOutcome? outcome = null;

            try
            {
                outcome = await _actions.RunAsync(marks, rule, stop);
                Outcome.Say(StatusLine.Said(outcome.Summary));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                Outcome.Say(StatusLine.Said(
                    "The rule stopped part-way. The marks it made before it stopped stay, and each can be undone."));
            }

            await ShowMarkedAsync(marks.Keeping, outcome?.Untouched.ToDictionary(pair => pair.Group, pair => pair.Reason) ?? []);
        });
    }

    private bool CanClearMarks() => WhyCannotMark is null && Marked > 0;

    /// <summary>Unmark every copy, which is always allowed.</summary>
    [RelayCommand(CanExecute = nameof(CanClearMarks))]
    private void ClearMarks()
    {
        if (!CanClearMarks())
        {
            return;
        }

        foreach (var group in _groups)
        {
            group.Marks.Clear();
            group.MarksChanged(markedSpace: 0);
        }

        Outcome.Say(StatusLine.Empty);
        Counted();
    }

    private bool CanRemove() => WhyCannotAct is null && Marked > 0;

    /// <summary>Move the marked copies to the Recycle Bin, once the user confirms the list.</summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task MoveToRecycleBinAsync() => RemoveAsync(ExploreRemovalMode.RecycleBin);

    /// <summary>Delete the marked copies permanently, the deliberate second choice, once the user confirms the list.</summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task DeletePermanentlyAsync() => RemoveAsync(ExploreRemovalMode.Permanent);

    private bool CanStop() => _stopping is not null;

    /// <summary>
    /// Stop the rule between groups, keeping the marks it made, or the removal at the next copy,
    /// reporting and checking what has gone.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _stopping?.Cancel();

    private async Task RemoveAsync(ExploreRemovalMode mode)
    {
        if (!CanRemove())
        {
            return;
        }

        var marks = _marks!;

        await StoppableAsync(async (stop, work) =>
        {
            try
            {
                // Asked again once the confirmation is built and once the user answers: the locations
                // stay open to change meanwhile, and a role changed then makes these marks stale.
                var answer = await _actions.RemoveAsync(marks, mode, () => _stale, stop);

                // The marks were judged again for the confirmation, so each copy says what it was judged.
                await ShowJudgedAsync(marks.Keeping);

                var rows = _groups.SelectMany(group => group).ToDictionary(row => row.Copy.Identity);

                // Whether or not anything was asked: a copy the bin cannot take stays either way.
                foreach (var copy in answer.Confirmation.Staying)
                {
                    rows[copy.Copy.Identity].Removal(copy.Why);
                }

                foreach (var copy in answer.Report?.Copies ?? [])
                {
                    rows[copy.Copy.Identity].Removal(copy.Message);
                }

                Outcome.Say(answer.Statement);

                // A check left unpassed is §5.6 failing to prove the removal left what it had to.
                if (answer.Statement.Checks.Count > 0)
                {
                    work.Fail();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                Outcome.Say(StatusLine.Said("Stopped before anything was removed."));
            }
        });
    }

    /// <summary>
    /// Show each copy and group as <paramref name="keeping"/> judges them, asked off this thread since
    /// it asks the policy of every copy.
    /// </summary>
    private async Task ShowJudgedAsync(CopyKeeping keeping)
    {
        List<DuplicateCopyRow> copies = [.. _groups.SelectMany(group => group)];
        List<DuplicateGroupRow> groups = [.. _groups];

        // Not stopped by the page's Stop: it shows a judgement already made, and rows left half
        // updated would show some copies as the machine was and others as it is.
        var (standings, notes, spaces) = await Task.Run(
            () => (
                copies.Select(row => keeping.Standing(row.Copy)).ToList(),
                groups.Select(group => group.Marks.WhyNothingCanBeKept(keeping)).ToList(),
                groups.Select(group => group.CountMarked(keeping)).ToList()),
            CancellationToken.None);

        for (var i = 0; i < copies.Count; i++)
        {
            copies[i].Show(standings[i]);
        }

        for (var i = 0; i < groups.Count; i++)
        {
            groups[i].Noted(notes[i]);
            groups[i].Counted(spaces[i]);
        }
    }

    /// <summary>
    /// Show every group's marks after a rule changed them: why the rule left a group as it was, from
    /// <paramref name="untouched"/>, or else why no copy there can be kept, and what its marks occupy.
    /// Asked off this thread, as <see cref="ShowJudgedAsync"/> is, since it asks the policy of every
    /// copy, and not stopped by the page's Stop, which would leave some groups showing marks undone.
    /// </summary>
    private async Task ShowMarkedAsync(CopyKeeping keeping, Dictionary<GroupMarks, string> untouched)
    {
        List<DuplicateGroupRow> groups = [.. _groups];

        var shown = await Task.Run(
            () => groups
                .Select(group => (
                    Note: untouched.GetValueOrDefault(group.Marks) ?? group.Marks.WhyNothingCanBeKept(keeping),
                    Space: group.CountMarked(keeping)))
                .ToList(),
            CancellationToken.None);

        for (var i = 0; i < groups.Count; i++)
        {
            groups[i].Noted(shown[i].Note);
            groups[i].MarksChanged(shown[i].Space);
        }
    }


    /// <summary>Run <paramref name="action"/> with every mark, rule and removal held until it ends, and Stop open meanwhile.</summary>
    private async Task StoppableAsync(Func<CancellationToken, BusySpan, Task> action)
    {
        using var stopping = new CancellationTokenSource();
        _stopping = stopping;
        StopCommand.NotifyCanExecuteChanged();

        try
        {
            await RunningAsync(work => action(stopping.Token, work));
        }
        finally
        {
            _stopping = null;
            StopCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Run <paramref name="action"/> with every mark, rule and removal held until it ends.</summary>
    private async Task RunningAsync(Func<BusySpan, Task> action)
    {
        IsBusy = true;
        var work = _work.Begin();

        try
        {
            Refresh();

            await action(work);
        }
        finally
        {
            IsBusy = false;
            work.Dispose();
            Counted();
        }
    }

    private void Counted()
    {
        OnPropertyChanged(nameof(MarkedFigure));
        Refresh();
    }

    /// <summary>Tell every control that asks whether marking, a rule or a removal is open now.</summary>
    private void Refresh()
    {
        OnPropertyChanged(nameof(WhyClosed));
        OnPropertyChanged(nameof(HasWhyClosed));
        OnPropertyChanged(nameof(CanMarkByRule));
        RunRuleCommand.NotifyCanExecuteChanged();
        ClearMarksCommand.NotifyCanExecuteChanged();
        MoveToRecycleBinCommand.NotifyCanExecuteChanged();
        DeletePermanentlyCommand.NotifyCanExecuteChanged();

        var mayMark = WhyCannotMark is null;

        if (mayMark != _mayMark)
        {
            _mayMark = mayMark;

            foreach (var copy in _groups.SelectMany(group => group))
            {
                copy.GateChanged();
            }
        }
    }
}
