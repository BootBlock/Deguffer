using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Choosing;
using Deguffer.Core.Configuration;
using Deguffer.Core.Providers;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The list behind the Scan button's drop-down: every location Deguffer recognises, grouped by
/// category, with the ones the user chose to scan on their own ticked.
///
/// <para>The choice is stored as the user makes it (<see cref="AppPreferences.ChosenToScan"/>), and
/// which rows are shown under which heading is Core's (<see cref="ProviderListing"/>). This holds the
/// rows, the search and the wiring between them.</para>
/// </summary>
public sealed partial class ScanChoiceViewModel : ObservableObject
{
    private readonly IReadOnlyList<ICleanupProvider> _providers;
    private readonly PreferenceService _preferences;
    private readonly Dictionary<ICleanupProvider, ScanChoiceRow> _rows = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ProviderCategory, ScanChoiceGroup> _groups = [];
    private ProviderChoice _choice;

    /// <param name="providers">Every location a full scan measures, in the order it measures them.</param>
    public ScanChoiceViewModel(IReadOnlyList<ICleanupProvider> providers, PreferenceService preferences)
    {
        _providers = providers;
        _preferences = preferences;
        _choice = preferences.Current.ChosenToScan;

        foreach (var provider in providers)
        {
            _rows[provider] = new ScanChoiceRow(this, provider);
        }

        foreach (var category in Enum.GetValues<ProviderCategory>())
        {
            _groups[category] = new ScanChoiceGroup(this, category);
        }

        Missing.Changed += (_, provider) => _rows.GetValueOrDefault(provider)?.Refresh();

        foreach (var row in _rows.Values)
        {
            row.Refresh();
        }

        Show();
    }

    /// <summary>
    /// Which locations the last scan that measured them found not installed. The Storage page records
    /// each row into it as the row lands.
    /// </summary>
    public MissingProviders Missing { get; } = new();

    /// <summary>The headings the search leaves something under, each holding the rows it shows.</summary>
    public ObservableCollection<ScanChoiceGroup> Shown { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    /// <summary>Whether the search hides every location, so the list can say so rather than stand empty.</summary>
    public bool HasNoMatches => Shown.Count == 0;

    /// <summary>The chosen locations, in the order a full scan measures them.</summary>
    public IReadOnlyList<ICleanupProvider> Chosen => _choice.Of(_providers);

    public bool CanScan => Chosen.Count > 0;

    public string ChosenSummary => $"{Chosen.Count} of {_providers.Count} locations chosen";

    /// <summary>
    /// Whether the last change could not be written to disk. The choice still applies until Deguffer
    /// closes, and the list says that it will not be remembered.
    /// </summary>
    [ObservableProperty]
    public partial bool SaveFailed { get; private set; }

    /// <summary>Raised whenever the choice changes, so a command that scans it can re-ask whether it may run.</summary>
    public event EventHandler? ChoiceChanged;

    public bool IsChosen(ICleanupProvider provider) => _choice.Contains(provider);

    /// <summary>Add <paramref name="providers"/> to the choice or take them out of it, and store it.</summary>
    public void Choose(IEnumerable<ICleanupProvider> providers, bool chosen)
    {
        var changed = _choice.With(providers, chosen);
        if (changed == _choice)
        {
            return;
        }

        _choice = changed;
        SaveFailed = !_preferences.Update(current => current with { ChosenToScan = changed });

        foreach (var row in _rows.Values)
        {
            row.Refresh();
        }

        foreach (var group in Shown)
        {
            group.Refresh();
        }

        OnPropertyChanged(nameof(Chosen));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(ChosenSummary));
        ChoiceChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSearchChanged(string value) => Show();

    private void Show()
    {
        var listed = ProviderListing.Group(_providers, Search);

        foreach (var group in listed)
        {
            _groups[group.Category].Show([.. group.Providers.Select(provider => _rows[provider])]);
        }

        LiveList.Show(Shown, [.. listed.Select(group => _groups[group.Category])], group => group.Category);
        OnPropertyChanged(nameof(HasNoMatches));
    }
}
