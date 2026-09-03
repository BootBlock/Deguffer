using System.Collections.ObjectModel;
using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// What one Storage row actually is: whose files these are, what they are for, whether to remove
/// them, and — on the second tab — exactly what removing them would do.
///
/// It is a control rather than a panel built in code for the reason
/// <see cref="CleanConfirmationView"/> is: <c>Application.Current.Resources[key]</c> in C# snapshots
/// the brushes of whatever theme is current and does not follow a repaint. Every claim it makes
/// comes from the provider through the <see cref="FindingViewModel"/>; nothing here decides
/// anything.
/// </summary>
public sealed partial class ProviderInfoView : UserControl
{
    /// <summary>
    /// How long the fill may run before the reader is told something is happening. Below this a
    /// ring is a flash rather than an explanation.
    /// </summary>
    private static readonly TimeSpan RingAppearsAfter = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many rows are added between pauses. The number matters only in that it is small enough
    /// for a page to be quick and large enough that the pauses are not the cost — see
    /// <see cref="FillContentsAsync"/> for why the fill is paged at all.
    /// </summary>
    private const int RowsPerPage = 50;

    private readonly ObservableCollection<StepViewModel> _steps = [];
    private readonly ObservableCollection<string> _notes = [];

    private bool _contentsAsked;

    /// <summary>
    /// Assigned before InitializeComponent, so no x:Bind can evaluate against a null model whatever
    /// the framework's initialisation order does next — the same order CleanPage relies on.
    /// </summary>
    public ProviderInfoView(FindingViewModel finding)
    {
        Finding = finding;
        InitializeComponent();

        // Bound here rather than in markup because the two collections are this control's own
        // working state. Binding them in markup would mean exposing each as a public property for
        // the sake of one x:Bind that nothing outside this file reads.
        StepList.ItemsSource = _steps;
        NoteList.ItemsSource = _notes;
    }

    public FindingViewModel Finding { get; }

    /// <summary>
    /// The Contents tab pays for itself only when it is opened, which is the whole reason the two
    /// questions are on separate tabs. Filled once: the plan behind it does not change while the
    /// dialog is up, and re-filling would duplicate every row.
    /// </summary>
    private async void OnSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_contentsAsked && ReferenceEquals(Sections.SelectedItem, ContentsSection))
        {
            _contentsAsked = true;
            await FillContentsAsync();
        }
    }

    /// <summary>
    /// List what this row would remove, and put a ring up if that takes longer than a moment.
    ///
    /// The rows are added a page at a time rather than in one assignment, and the pause between
    /// pages is what makes the ring possible at all: realising list items is UI-thread work, so a
    /// single assignment holds the thread for the whole fill, and a ring raised while the thread is
    /// held is a ring that never paints. Paging also keeps the dialog itself answering — a plan
    /// with one step per workspace runs to hundreds of rows.
    /// </summary>
    private async Task FillContentsAsync()
    {
        using var filled = new CancellationTokenSource();

        var ring = RevealRingAfterAsync(filled.Token);

        foreach (var step in Finding.Steps)
        {
            _steps.Add(step);

            if (_steps.Count % RowsPerPage == 0)
            {
                await Task.Yield();
            }
        }

        foreach (var note in Finding.Notes)
        {
            _notes.Add(note);
        }

        filled.Cancel();

        // Awaited rather than abandoned, so the ring cannot be hidden here and then shown by a
        // continuation that had already been scheduled.
        await ring;

        ContentsRing.Visibility = Visibility.Collapsed;
    }

    private async Task RevealRingAfterAsync(CancellationToken filled)
    {
        try
        {
            await Task.Delay(RingAppearsAfter, filled);
        }
        catch (OperationCanceledException)
        {
            // The list arrived first, which is the ordinary case. Nothing to show.
            return;
        }

        ContentsRing.Visibility = Visibility.Visible;
    }
}
