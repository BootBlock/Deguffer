using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Execution;
using Deguffer.Core.Memory.Acting;

namespace Deguffer.App.ViewModels;

/// <summary>
/// What one close did, on a surface of its own that the user dismisses (§7.2.1).
///
/// <para><b>It is not the page's status line, and that is the whole point of it.</b> The page takes a
/// reading every couple of seconds and clears its reading notes each time, so a close's report put
/// there would be wiped inside one cadence. §5.6 exists to leave the user evidence, and evidence that
/// disappears before it is read is not evidence — so this stands until the user takes it down.</para>
///
/// <para>It writes none of the words. The sentence, the figures and every §5.6 check come from
/// <see cref="CloseReport"/> in Core, where a test can hold Deguffer to them.</para>
/// </summary>
public sealed partial class MemoryCloseReport : ObservableObject
{
    /// <summary>What happened, in <see cref="CloseReport.Statement"/>'s words. Empty when nothing is reported.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    public partial string Statement { get; set; } = string.Empty;

    /// <summary>Commit charge and available memory, before and after where the program exited. Empty on a refusal.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFigures))]
    public partial string Figures { get; set; } = string.Empty;

    /// <summary>
    /// Whether a §5.6 assertion did not pass, which colours the sentence. The sentence says it in
    /// words as well, because §6.5 requires it to survive a flat background.
    /// </summary>
    [ObservableProperty]
    public partial bool Failed { get; set; }

    /// <summary>
    /// Whether the program has been asked and Deguffer is still watching. While this is true no
    /// second close is offered (§7.2.1).
    /// </summary>
    [ObservableProperty]
    public partial bool IsWatching { get; set; }

    /// <summary>
    /// §5.6's four kinds of line: what was sent and where, what could not have been ended and was
    /// looked for, what was expected to exit, and what exited that Deguffer sent nothing to.
    /// </summary>
    public ObservableCollection<VerificationCheck> Checks { get; } = [];

    public bool HasReport => Statement.Length > 0;

    public bool HasFigures => Figures.Length > 0;

    public bool HasChecks => Checks.Count > 0;

    /// <summary>
    /// Raised when the user takes the report down, which is one of §7.2.1's three ends of a watch.
    /// The owner stops watching; this type only stops showing.
    /// </summary>
    public event EventHandler? Dismissed;

    /// <summary>Show what a close has come to, whether it is still being watched or has finished.</summary>
    public void Show(CloseReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Statement = report.Statement;
        Figures = report.Figures;
        Failed = !report.Verification.Passed;
        IsWatching = report.State == CloseState.Watching;

        ShowChecks(report.Verification.Checks);
    }

    /// <summary>
    /// State a sentence with no close behind it: a refusal decided at the moment of the action, or a
    /// confirmation the user declined. Nothing was sent, so there is nothing to assert and no figure
    /// to take.
    /// </summary>
    public void Say(string sentence)
    {
        ArgumentException.ThrowIfNullOrEmpty(sentence);

        Statement = sentence;
        Figures = string.Empty;
        Failed = false;
        IsWatching = false;

        ShowChecks([]);
    }

    /// <summary>Take the report down without ending anything. See <see cref="Dismiss"/>.</summary>
    public void Clear()
    {
        Statement = string.Empty;
        Figures = string.Empty;
        Failed = false;
        IsWatching = false;

        ShowChecks([]);
    }

    /// <summary>
    /// The user has read it. Taking it down ends the watch as well, because §7.2.1 gives the watch
    /// three ends and this is one of them: there is no report left to put the answer on.
    /// </summary>
    [RelayCommand]
    private void Dismiss()
    {
        Clear();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void ShowChecks(IReadOnlyList<VerificationCheck> checks)
    {
        Checks.Clear();

        foreach (var check in checks)
        {
            Checks.Add(check);
        }

        OnPropertyChanged(nameof(HasChecks));
    }
}
