using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Duplicates;
using Deguffer.Core.Exploring.Acting;

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

    private const string ChooseFolder = "Choose the folder the rule is about.";

    private readonly DuplicateActions _actions;
    private readonly IReadOnlyList<DuplicateGroupRow> _groups;

    private DuplicateMarks? _marks;
    private DuplicateSearch? _asked;
    private bool _searching;
    private string? _stale;
    private bool _spent;
    private bool _mayMark;
    private CancellationTokenSource? _removing;

    /// <param name="groups">The page's groups, in the order it shows them.</param>
    public DuplicateMarkingViewModel(DuplicateActions actions, IReadOnlyList<DuplicateGroupRow> groups)
    {
        _actions = actions;
        _groups = groups;
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
    [NotifyPropertyChangedFor(nameof(RuleFolderShown))]
    public partial string RuleFolder { get; set; } = string.Empty;

    /// <summary>The folder a folder rule is about, or what to do where none is chosen.</summary>
    public string RuleFolderShown => RuleFolder.Length > 0 ? RuleFolder : ChooseFolder;

    public bool RuleNeedsFolder => RuleIndex >= 3;

    /// <summary>Whether a rule, a confirmation or a removal is running, so the page waits for it.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>What the last rule or removal did, or an empty string.</summary>
    [ObservableProperty]
    public partial string Outcome { get; private set; } = string.Empty;

    /// <summary>Why nothing can be marked, ruled or removed now, or an empty string where something can.</summary>
    public string WhyClosed => WhyCannotAct ?? _marks?.WhyRulesCannotMark ?? string.Empty;

    /// <summary>How many copies are marked, in words, or an empty string where none are.</summary>
    public string MarkedSummary => Marked switch
    {
        0 => string.Empty,
        1 => "1 copy marked.",
        var count => $"{count:N0} copies marked.",
    };

    private int Marked => _groups.Sum(group => group.Marks.MarkedCount);

    /// <summary>Why no mark may change now, or null where marks may change.</summary>
    private string? WhyCannotMark => _marks is null ? string.Empty : _stale ?? (_spent ? Spent : null) ?? (IsBusy ? Busy : null);

    /// <summary>Why no rule or removal may start now, or null where one may.</summary>
    private string? WhyCannotAct => WhyCannotMark ?? (_searching ? StillSearching : null);

    /// <summary>A search began, for <paramref name="asked"/>: what the last one found is gone.</summary>
    internal void Started(DuplicateSearch asked)
    {
        _marks = null;
        _asked = asked;
        _searching = true;
        _stale = null;
        _spent = false;
        Outcome = string.Empty;
        Refresh();
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
        _searching = false;
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

        Counted();

        return refused;
    }

    /// <summary>Whether marks may change now, which each copy's check box asks.</summary>
    internal bool MayMark() => _mayMark;

    private bool CanRunRule() =>
        WhyCannotAct is null && _marks!.WhyRulesCannotMark is null && _groups.Count > 0 && (!RuleNeedsFolder || RuleFolder.Length > 0);

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

        await RunningAsync(async () =>
        {
            var outcome = await _actions.RunAsync(marks, rule);
            var untouched = outcome.Untouched.ToDictionary(pair => pair.Group, pair => pair.Reason);

            foreach (var group in _groups)
            {
                group.Noted(untouched.GetValueOrDefault(group.Marks) ?? group.Marks.WhyNothingCanBeKept(marks.Keeping));

                foreach (var copy in group.Copies)
                {
                    copy.MarksChanged();
                }
            }

            Outcome = outcome.Summary;
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

            foreach (var copy in group.Copies)
            {
                copy.MarksChanged();
            }
        }

        Outcome = string.Empty;
        Counted();
    }

    private bool CanRemove() => WhyCannotAct is null && Marked > 0;

    /// <summary>Move the marked copies to the Recycle Bin, once the user confirms the list.</summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task MoveToRecycleBinAsync() => RemoveAsync(ExploreRemovalMode.RecycleBin);

    /// <summary>Delete the marked copies permanently, the deliberate second choice, once the user confirms the list.</summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task DeletePermanentlyAsync() => RemoveAsync(ExploreRemovalMode.Permanent);

    private bool CanStopRemoving() => _removing is not null;

    /// <summary>Stop the removal at the next copy. What has gone is reported and checked.</summary>
    [RelayCommand(CanExecute = nameof(CanStopRemoving))]
    private void StopRemoving() => _removing?.Cancel();

    private async Task RemoveAsync(ExploreRemovalMode mode)
    {
        if (!CanRemove())
        {
            return;
        }

        var marks = _marks!;
        using var removing = new CancellationTokenSource();

        await RunningAsync(async () =>
        {
            _removing = removing;
            StopRemovingCommand.NotifyCanExecuteChanged();

            try
            {
                var answer = await _actions.RemoveAsync(marks, mode, removing.Token);

                // The marks were judged again for the confirmation, so each copy says what it was judged.
                await ShowJudgedAsync(marks.Keeping);

                if (answer.Report is { } report)
                {
                    var rows = _groups.SelectMany(group => group.Copies).ToDictionary(row => row.Copy.Identity);

                    foreach (var copy in report.Copies)
                    {
                        rows[copy.Copy.Identity].Removal(copy.Message);
                    }

                    _spent = true;
                }

                Outcome = answer.Summary;
            }
            catch (OperationCanceledException) when (removing.IsCancellationRequested)
            {
                Outcome = "Stopped before anything was removed.";
            }
            finally
            {
                _removing = null;
                StopRemovingCommand.NotifyCanExecuteChanged();
            }
        });
    }

    /// <summary>
    /// Show each copy and group as <paramref name="keeping"/> judges them, asked off this thread since
    /// it asks the policy of every copy.
    /// </summary>
    private async Task ShowJudgedAsync(CopyKeeping keeping)
    {
        List<DuplicateCopyRow> copies = [.. _groups.SelectMany(group => group.Copies)];
        List<DuplicateGroupRow> groups = [.. _groups];

        var (standings, notes) = await Task.Run(() => (
            copies.Select(row => CopyStanding.Of(row.Copy, keeping)).ToList(),
            groups.Select(group => group.Marks.WhyNothingCanBeKept(keeping)).ToList()));

        for (var i = 0; i < copies.Count; i++)
        {
            copies[i].Show(standings[i]);
        }

        for (var i = 0; i < groups.Count; i++)
        {
            groups[i].Noted(notes[i]);
        }
    }

    /// <summary>Run <paramref name="action"/> with every mark, rule and removal held until it ends.</summary>
    private async Task RunningAsync(Func<Task> action)
    {
        IsBusy = true;
        Refresh();

        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
            Counted();
        }
    }

    private void Counted()
    {
        OnPropertyChanged(nameof(MarkedSummary));
        Refresh();
    }

    /// <summary>Tell every control that asks whether marking, a rule or a removal is open now.</summary>
    private void Refresh()
    {
        OnPropertyChanged(nameof(WhyClosed));
        RunRuleCommand.NotifyCanExecuteChanged();
        ClearMarksCommand.NotifyCanExecuteChanged();
        MoveToRecycleBinCommand.NotifyCanExecuteChanged();
        DeletePermanentlyCommand.NotifyCanExecuteChanged();

        var mayMark = WhyCannotMark is null;

        if (mayMark != _mayMark)
        {
            _mayMark = mayMark;

            foreach (var copy in _groups.SelectMany(group => group.Copies))
            {
                copy.GateChanged();
            }
        }
    }
}
