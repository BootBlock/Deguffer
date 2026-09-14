using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Memory;
using Deguffer.Core.Memory.Acting;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What the user picked out by hand on the Memory page, and the one thing §7.2.1 lets them do with
/// it: ask that program to close itself.
///
/// <para><b>One program at a time, picked by hand, and nothing else.</b> Memory never pre-selects,
/// never orders anything by how closable it is, and never acts on more than what was picked (§7.2).
/// There is one verb here and it does not grow a second: no terminate, no bulk close, and no
/// preference that adds either.</para>
///
/// <para>It decides nothing. What may be closed is <see cref="MemoryActionPolicy"/>'s, what the user
/// is told is <see cref="MemoryClosePrompt"/>'s, and what happened is <see cref="CloseReport"/>'s —
/// all in Core, all provable without a WinUI host.</para>
/// </summary>
public sealed partial class MemorySelection : ObservableObject
{
    /// <summary>
    /// What the page says while Windows is being asked about the program just picked.
    ///
    /// <para>Said rather than left blank, because the answer takes an open of the process and six
    /// questions about it, and a selection with no sentence under it reads as a program nothing has
    /// to say about.</para>
    /// </summary>
    private const string Asking = "Deguffer is asking Windows about this program.";

    /// <summary>
    /// What the page says once Windows has refused to answer what §7.2.1 decides from.
    ///
    /// <para>A verdict rather than a bare sentence, for the reason
    /// <see cref="ExploreActions.Failed"/> is one: it is the selection's answer, so it has to reach
    /// the button's own path as an answer rather than leave the selection with no verdict at
    /// all.</para>
    /// </summary>
    private static readonly MemoryVerdict Unanswered = MemoryVerdict.Refuse(
        "Windows would not answer what Deguffer has to know before it asks a program to close, so it "
        + "will not ask this one.");

    private readonly MemoryActions _actions;

    private MemoryTree? _tree;
    private int? _node;

    /// <summary>
    /// The process the selection is about, and the reading it was picked from, kept together because
    /// §7.2.1 identifies a process by both: the identifier alone belongs to whatever holds it now.
    /// </summary>
    private ProcessMemory? _target;
    private MemorySnapshot? _pickedFrom;

    private MemoryVerdict? _verdict;

    /// <summary>
    /// Which selection the verdict being read belongs to. A read runs off the window's thread, and a
    /// reader who picks a second program before the first answer lands must not be told about the
    /// first.
    /// </summary>
    private int _generation;

    private CancellationTokenSource? _asking;

    /// <summary>
    /// The decision being read for the current selection, so a press that beats Windows' answer can
    /// wait for it. Explore waits for its policy on the path that deletes for the same reason: an
    /// action decided against an answer that has not arrived is the one thing deferring it must never
    /// buy.
    /// </summary>
    private Task? _deciding;

    /// <summary>Ends the watch, which the user does by dismissing the report and by leaving the page.</summary>
    private CancellationTokenSource? _watching;

    /// <summary>
    /// Whether the user took the report down while this close was still being watched. The close goes
    /// on to its end and writes its evidence, but putting that back on screen would undo the
    /// dismissal.
    /// </summary>
    private bool _dismissed;

    private bool _closing;

    public MemorySelection(MemoryActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        _actions = actions;

        Report.Dismissed += (_, _) =>
        {
            _dismissed = true;
            StopWatching();
            CloseCommand.NotifyCanExecuteChanged();
        };

        Report.PropertyChanged += OnReportChanged;
    }

    /// <summary>What one close did, and the §5.6 evidence behind it. Outlives every reading (§7.2.1).</summary>
    public MemoryCloseReport Report { get; } = new();

    /// <summary>
    /// Which node is selected, so the list and the picture can both be put back in step with it.
    ///
    /// <para>Named for the reason <see cref="ExploreSelection.Nodes"/> is: a <c>ListView</c> keeps
    /// its own copy and drops it whenever the rows are rewritten, which on this page is every couple
    /// of seconds, so something has to say which of the two copies is right.</para>
    /// </summary>
    public int? Node => _node;

    /// <summary>What is selected, in words. The only thing that names it.</summary>
    public string Label =>
        _tree is { } tree && _node is { } node ? $"Selected: {MemoryText.Name(tree, node)}" : string.Empty;

    /// <summary>How much it holds, said apart from what it is, so a long name cannot push the figure off the line.</summary>
    public string Figures =>
        _tree is { } tree && _node is { } node ? MemoryText.Figures(tree, node) : string.Empty;

    /// <summary>
    /// What Deguffer will do about the selection, or why it will not.
    ///
    /// <para>Stated as soon as something is picked rather than after the user tries. §7.2.1: <b>a
    /// refusal is a sentence on the row, never a disabled button</b>, and its refusal table gives a
    /// reader no way to guess which of its rows applied.</para>
    /// </summary>
    public string Note { get; private set; } = string.Empty;

    public bool HasNote => Note.Length > 0;

    public bool HasSelection => _node is not null;

    /// <summary>Point at a tree and select nothing. Called on every navigation: a selection in one part is not one in the next.</summary>
    public void Show(MemoryTree? tree)
    {
        _tree = tree;
        Select(null);
    }

    /// <summary>
    /// Move to the reading that replaces the one on screen, keeping the selection where it still names
    /// the same process.
    ///
    /// <para>By <see cref="MemoryPlace"/>'s rule, which is identifier and creation time: every reading
    /// renumbers the nodes, and a number carried across would point at whatever now sits there. A
    /// selection that does not carry is dropped rather than replaced — the program exited, and putting
    /// another in its place would be Deguffer choosing the target.</para>
    ///
    /// <para>Nothing is asked of Windows here. The verdict was read when the user picked the program,
    /// and reading it again twice a second would open that process thirty times a minute for an answer
    /// nobody asked for (§7.2.1).</para>
    /// </summary>
    public void Carry(MemoryTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        // Read against the tree the number belongs to, before that becomes the arriving one.
        var carried = _node is { } node ? MemoryPlace.TryCarry(_tree, node, tree) : null;
        var had = _node;

        _tree = tree;

        if (had is null)
        {
            // Nothing was picked, and a reading arriving does not pick anything (§7.2). Said by
            // returning rather than by restating: every reading would otherwise announce a selection
            // that has not changed, twice a second, for the life of the page.
            return;
        }

        if (carried is null)
        {
            Select(null);
            return;
        }

        _node = carried;

        Restate();
    }

    /// <summary>
    /// What the user picked out by hand, by node. Null clears it.
    ///
    /// <para>By node rather than by row, because the list is not the only view that can select: a hit
    /// on the picture can land several levels below the node the page is showing, where there is no
    /// row at all.</para>
    /// </summary>
    public void Select(int? node)
    {
        _generation++;

        _asking?.Cancel();

        _node = _tree is { } tree && node is { } picked && picked >= 0 && picked < tree.NodeCount
            ? picked
            : null;

        _verdict = null;
        _target = null;
        _pickedFrom = null;
        _deciding = null;

        if (_tree is { } current && _node is { } chosen)
        {
            _target = MemoryTarget.Of(current, chosen);
            _pickedFrom = current.Snapshot;
        }

        // A sentence rather than an empty selection, for the reason every refusal is one
        // (§7.2.1): a reader who picked a shape and found nothing to press learns nothing about
        // why. The words are Core's, as every other refusal's are.
        _verdict = _target is null && _node is not null ? MemoryTarget.NotAProgram : null;

        Note = _target is not null ? Asking : _verdict?.Reason ?? string.Empty;

        Restate();

        if (_target is { } about && _pickedFrom is { } from)
        {
            _deciding = AskAsync(about, from, _generation);
        }
    }

    /// <summary>
    /// End the watch because the page has been left, which is one of §7.2.1's three ends. The close
    /// itself is not called off — the messages have gone — and what it comes to is still written to
    /// the report, for a reader who comes back to the page.
    /// </summary>
    public void Leave() => StopWatching();

    /// <summary>
    /// §7.2.1's one action: ask the program to close itself, the way its own close button does.
    ///
    /// <para>Offered whenever a node is selected, refused or not. A refusal is answered with the
    /// sentence that says why rather than with a button that does nothing, and no dialog is raised
    /// over something that would then be refused.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanClose))]
    private async Task CloseAsync()
    {
        var generation = _generation;

        _closing = true;

        CloseCommand.NotifyCanExecuteChanged();

        try
        {
            // Pressed before Windows answered. Waited for rather than answered from what is known so
            // far, because this is the path that sends the message: a close decided against an
            // answer that has not arrived is the one thing reading the machine off the window's
            // thread must never buy. Explore waits for its policy on the path that deletes, for the
            // same reason.
            if (_deciding is { IsCompleted: false } deciding)
            {
                await deciding;
            }

            if (generation != _generation)
            {
                // Something else was picked while the answer was on its way, so this press is about
                // a selection that has gone. §7.2 acts on what the user picked and on nothing else.
                return;
            }

            await AskAndCloseAsync();
        }
        finally
        {
            _closing = false;

            CloseCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task AskAndCloseAsync()
    {
        // One question — is there a program here, and may it be asked? A verdict is only allowed for
        // a program picked out of a reading, so the three terms are three spellings of one state
        // rather than a guard against something that cannot happen.
        if (_verdict is not { IsAllowed: true } allowed
            || _target is not { } target
            || _pickedFrom is not { } snapshot)
        {
            // The reason is already under the selection. Saying it here as well is what answers the
            // press: a button that appears to do nothing teaches nothing at all.
            Report.Say(_verdict?.Reason ?? Unanswered.Reason);
            return;
        }

        _dismissed = false;

        StopWatching();

        var watch = new CancellationTokenSource();

        _watching = watch;

        try
        {
            var watching = new Progress<CloseReport>(report =>
            {
                if (!_dismissed)
                {
                    Report.Show(report);
                }
            });

            var attempt = await _actions.CloseAsync(
                target, allowed.Windows.Count, snapshot.Services.Listing, watching, watch.Token);

            if (_dismissed)
            {
                return;
            }

            switch (attempt)
            {
                case null:
                    // Declined. Saying so beats leaving the previous sentence standing, which
                    // somebody who has just dismissed a dialog reads as the outcome of it.
                    Report.Say($"{target.Named} was not asked to close. Nothing was sent.");
                    break;

                case { Report: { } done }:
                    Report.Show(done);
                    break;

                case { Verdict: var refused }:
                    // The second decision, made with the handle held. The machine moved between the
                    // selection and the confirmation, so what is on screen is out of date and this is
                    // the answer.
                    Report.Say(refused.Reason);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The page was left, or the report was taken down, before anything was posted. Nothing
            // was sent, so there is nothing to report about it.
        }
        catch (Win32Exception ex)
        {
            // Windows would not answer one of the calls a close is decided from. Nothing had been
            // posted: everything that can fail this way happens before the first message.
            Report.Say($"Windows would not answer: {ex.Message} Nothing was asked to close.");
        }
        finally
        {
            // The watch is over either way, so the source it ran on goes now rather than waiting for
            // the next close to take it down. Only where it is still this close's own: a dismissal
            // has already replaced it with nothing.
            if (ReferenceEquals(_watching, watch))
            {
                _watching = null;
            }

            watch.Dispose();
        }
    }

    /// <summary>
    /// Ask the policy about the program just picked, off the window's thread, and say what it
    /// answered — unless the reader has picked something else since.
    /// </summary>
    private async Task AskAsync(ProcessMemory target, MemorySnapshot snapshot, int generation)
    {
        var asking = new CancellationTokenSource();

        _asking = asking;

        try
        {
            var verdict = await _actions.VerdictAsync(snapshot, target, asking.Token);

            if (generation != _generation)
            {
                return;
            }

            _verdict = verdict;
            Note = verdict.Reason;

            Restate();
        }
        catch (OperationCanceledException)
        {
            // Another program was picked while this one was being asked about.
        }
        catch (Win32Exception ex)
        {
            // Recorded as well as shown: the sentence says the question went unanswered, and the log
            // says which call failed and how.
            App.Faults.Record("Deciding whether Memory may close a program", ex);

            if (generation != _generation)
            {
                return;
            }

            Note = "Windows would not answer what Deguffer has to know before it asks a program to "
                   + "close, so it will not ask this one.";

            Restate();
        }
        finally
        {
            if (ReferenceEquals(_asking, asking))
            {
                _asking = null;
            }

            asking.Dispose();
        }
    }

    private void OnReportChanged(object? sender, PropertyChangedEventArgs changed)
    {
        if (changed.PropertyName == nameof(MemoryCloseReport.IsWatching))
        {
            CloseCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// End the watch, and leave the close that started it to take its source down.
    ///
    /// <para>Cancelled here and disposed there, because the close is still running: the confirmation
    /// registers on this token to take a dialog off the screen, and registering on a source that has
    /// been disposed throws where cancelling one merely runs the callback at once.</para>
    /// </summary>
    private void StopWatching()
    {
        _watching?.Cancel();
        _watching = null;
    }

    private void Restate()
    {
        OnPropertyChanged(nameof(Node));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Figures));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(HasSelection));

        CloseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Whether a close may be offered at all. <b>One close at a time</b> (§7.2.1): while a watch is
    /// open there is no second one, so that one report is about one action.
    /// </summary>
    private bool CanClose() => _node is not null && !_closing && !Report.IsWatching;
}
