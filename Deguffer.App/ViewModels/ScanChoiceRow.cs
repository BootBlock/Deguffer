using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Providers;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One location in the list of what to scan: its tick, and whether the last scan that measured it
/// found it not installed. The tick is written through <see cref="ScanChoiceViewModel"/>, which
/// stores it and applies it only once it is stored, so the box shows what was stored rather than
/// what was clicked.
/// </summary>
public sealed partial class ScanChoiceRow : ObservableObject
{
    private readonly ScanChoiceViewModel _owner;
    private bool _isChosen;

    public ScanChoiceRow(ScanChoiceViewModel owner, ICleanupProvider provider)
    {
        _owner = owner;
        Provider = provider;
        ToggleAutomationId = $"ScanChoiceToggle.{provider.Id}";
    }

    public ICleanupProvider Provider { get; }

    public string Name => Provider.Name;

    /// <summary>
    /// The UI Automation id of the tick, from the provider's stable id. Not the Storage row's id: the
    /// drop-down opens over the Storage list, so one location is in both at once.
    /// </summary>
    public string ToggleAutomationId { get; }

    public bool IsChosen
    {
        get => _isChosen;
        set
        {
            if (value != _isChosen)
            {
                _owner.Choose([Provider], value);
            }
        }
    }

    /// <summary>Whether the last scan that measured this location found it not installed.</summary>
    [ObservableProperty]
    public partial bool WasMissing { get; private set; }

    /// <summary>What a screen reader says for the tick, which carries the mark the eye reads beside it.</summary>
    public string AccessibleName => WasMissing ? $"{Name}, not found last scan" : Name;

    partial void OnWasMissingChanged(bool value) => OnPropertyChanged(nameof(AccessibleName));

    /// <summary>Bring the tick and the mark up to what the owner holds.</summary>
    internal void Refresh()
    {
        var chosen = _owner.IsChosen(Provider);
        if (chosen != _isChosen)
        {
            _isChosen = chosen;
            OnPropertyChanged(nameof(IsChosen));
        }

        WasMissing = _owner.Missing.WasMissing(Provider);
    }

    /// <summary>Say the tick has changed even where it has not, so a box clicked into a state that did not hold reads it back.</summary>
    internal void Republish() => OnPropertyChanged(nameof(IsChosen));
}
