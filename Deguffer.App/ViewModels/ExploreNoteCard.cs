using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Exploring;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The notes in the corner of the Explore page's card: the four of them, and the button they
/// collapse into. Which are shown and whether one warns are <see cref="ExploreNotes"/>' to decide.
///
/// <para>Separate from <see cref="ExploreViewModel"/>, as <see cref="ExploreGrowth"/> is, because it
/// reads neither the tree on screen nor the node being looked at. Two of the notes are the
/// selection's, which this follows itself, and the page hands over the other two as they change.</para>
/// </summary>
public sealed partial class ExploreNoteCard : ObservableObject
{
    // Segoe Fluent Icons, by code point rather than pasted: a private-use glyph reads back as
    // nothing at all through a file tool, so the literal is the one form that can be checked.
    private const string InfoGlyph = "";
    private const string WarningGlyph = "";

    private readonly ExploreSelection _selection;

    public ExploreNoteCard(ExploreSelection selection)
    {
        _selection = selection;

        // What the card shows in its corner turns partly on a type this one does not speak for.
        // Without following it, picking something Explore will not remove while the notes are
        // collapsed would leave no button offering to say why (§7.1).
        _selection.PropertyChanged += OnSelectionChanged;
    }

    /// <summary>The sentence §5.5 requires beside a walked scan, or null when the table answered.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRouteNote))]
    public partial string? RouteNote { get; internal set; }

    partial void OnRouteNoteChanged(string? value) => Changed();

    public bool HasRouteNote => !string.IsNullOrEmpty(RouteNote);

    /// <summary>Whether the page states how what is on screen differs from what the View box names.</summary>
    [ObservableProperty]
    public partial bool HasViewNote { get; internal set; }

    partial void OnHasViewNoteChanged(bool value) => Changed();

    /// <summary>Whether the reader has collapsed the notes into their button. See <see cref="ExploreNotes.Dismissed"/>.</summary>
    [ObservableProperty]
    public partial bool Dismissed { get; set; }

    partial void OnDismissedChanged(bool value) => Changed();

    /// <summary>The four notes and the reader's choice to collapse them. See <see cref="ExploreNotes"/>.</summary>
    private ExploreNotes Notes =>
        new(_selection.HasNote, _selection.HasStaleNote, HasViewNote, HasRouteNote, Dismissed);

    /// <summary>
    /// What the collapsed button shows, and what it is called: both change where one of the notes is
    /// a warning (<see cref="ExploreNotes.Warns"/>), so neither is colour alone.
    /// </summary>
    public string ButtonGlyph => Notes.Warns ? WarningGlyph : InfoGlyph;

    /// <inheritdoc cref="ButtonGlyph"/>
    public string ButtonLabel => Notes.Warns
        ? "Show the notes about this scan. One of them is a warning."
        : "Show the notes about this scan";

    /// <summary>Whether the notes are on the card. See <see cref="ExploreNotes.ShowsPanel"/>.</summary>
    public bool ShowsPanel => Notes.ShowsPanel;

    /// <summary>Whether the button that brings them back is on the card. See <see cref="ExploreNotes.ShowsButton"/>.</summary>
    public bool ShowsButton => Notes.ShowsButton;

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs changed)
    {
        if (changed.PropertyName is nameof(ExploreSelection.HasNote) or nameof(ExploreSelection.HasStaleNote))
        {
            Changed();
        }
    }

    /// <summary>Say that what the notes hold has changed, whichever of the four it was.</summary>
    private void Changed()
    {
        OnPropertyChanged(nameof(ShowsPanel));
        OnPropertyChanged(nameof(ShowsButton));
        OnPropertyChanged(nameof(ButtonGlyph));
        OnPropertyChanged(nameof(ButtonLabel));
    }
}
