using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Execution;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The last clean's §5.6 safety check on the Storage page's result card: its verdict, the checks it
/// has something to answer for, and the copy of the run for a fault report.
///
/// <para>What the verdict says, and whether it is a clean pass, is <see cref="RunOutcome"/>'s. This
/// type holds it for as long as the run's figures stand and tells the card what to show.</para>
///
/// <para>The info bar cannot be the verdict's home. The bar is a single line describing whatever
/// happened most recently, and what happens immediately after a clean is the re-plan that follows it,
/// so a verdict left there is displaced by the next provider's progress line. §5.6 exists to turn
/// "I think it worked" into evidence the user sees, which a message visible for the length of one
/// rescan is not.</para>
/// </summary>
public sealed partial class SafetyCheckViewModel : ObservableObject
{
    /// <summary>The verdict, or null where no run's figures are on screen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(Statement),
        nameof(VerificationFailed),
        nameof(HasDetail),
        nameof(OffersDiagnosticsLink),
        nameof(OffersDiagnosticsMenu),
        nameof(ShowsDiagnosticsRow))]
    public partial RunOutcome? Outcome { get; private set; }

    /// <summary>
    /// Whether the last copy of the run's diagnostics went, or why not. Empty until the user copies,
    /// and emptied by the next run, so a new verdict never says an earlier run's report was copied.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDiagnosticsRow))]
    public partial string CopyResult { get; private set; } = string.Empty;

    /// <summary>The whole verdict in one line, as the info bar and a report state it.</summary>
    public string Statement => Outcome?.Statement ?? string.Empty;

    /// <summary>
    /// Whether the verdict is a failure, so the card can say so in more than words. The words carry
    /// it on their own (§6.5), and the glyph beside them says the same, so the colour is the third cue
    /// and never the only one.
    /// </summary>
    public bool VerificationFailed => Outcome?.VerificationFailed ?? false;

    /// <summary>Whether there is anything under the heading to say, so an empty line takes no room.</summary>
    public bool HasDetail => Outcome is { Detail.Length: > 0 };

    /// <summary>
    /// Whether the card offers the copy of the run as a link in plain view: wherever the check did not
    /// pass outright, because that is where a fault report may follow.
    /// </summary>
    public bool OffersDiagnosticsLink => Outcome is { Passed: false };

    /// <summary>
    /// Whether the card offers the copy behind a menu instead. On a pass a link in plain view suggests
    /// something went wrong, and the copy is still wanted now and then, for a figure that looks off.
    /// </summary>
    public bool OffersDiagnosticsMenu => Outcome is { Passed: true };

    /// <summary>
    /// Whether the row that holds the link and the copy's result is shown. On a pass it holds no link,
    /// and it opens only to say whether a copy made from the menu went.
    /// </summary>
    public bool ShowsDiagnosticsRow => OffersDiagnosticsLink || CopyResult.Length > 0;

    /// <summary>
    /// Every §5.6 check the run has something to answer for, in full and unabridged.
    ///
    /// <para>The verdict tells the user a protected path went missing and, when it is a failure, asks
    /// them to report it. Without this it asks them to report something it never told them: the paths,
    /// the reasons they were protected, and what was found instead. A request to report a fault, with
    /// no way to say what the fault was, is a dead end.</para>
    /// </summary>
    public ObservableCollection<VerificationCheck> Notes { get; } = [];

    /// <summary>Whether there is anything in that list, so the card shows it only when there is.</summary>
    public bool HasNotes => Notes.Count > 0;

    /// <summary>
    /// The last run written out for a GitHub issue, which the card's link copies. Empty where no run's
    /// figures are on screen. See <see cref="Core.Diagnostics.CleanRunReport"/>.
    /// </summary>
    public string Diagnostics { get; private set; } = string.Empty;

    /// <summary>Put a finished run's verdict on the card, with the checks behind it and its report.</summary>
    public void Show(RunOutcome outcome, IReadOnlyList<VerificationCheck> notes, string diagnostics)
    {
        Diagnostics = diagnostics;
        CopyResult = string.Empty;
        Outcome = outcome;

        // Written over rather than emptied and filled, so a second run that meets the same checks
        // leaves the panel alone instead of taking it down and putting it back up.
        LiveList.Rewrite(Notes, notes);
        OnPropertyChanged(nameof(HasNotes));
    }

    /// <summary>Take the verdict off the card, as the run's figures go.</summary>
    public void Clear()
    {
        Diagnostics = string.Empty;
        CopyResult = string.Empty;
        Outcome = null;
        Notes.Clear();
        OnPropertyChanged(nameof(HasNotes));
    }

    /// <summary>
    /// Record how a copy of <see cref="Diagnostics"/> went: <paramref name="refusal"/> is null when
    /// the clipboard took it, or the sentence saying why it did not.
    /// </summary>
    public void Copied(string? refusal) =>
        CopyResult = refusal ?? "Copied. Paste it into a GitHub issue.";
}
