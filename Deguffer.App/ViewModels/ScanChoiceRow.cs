using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Providers;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One location in the list of what to scan: its tick, and whether the last scan that measured it
/// found it not installed. The tick is written through <see cref="ScanChoiceViewModel"/>, which
/// stores it, so the box shows what was stored rather than what was clicked.
/// </summary>
public sealed partial class ScanChoiceRow : ObservableObject
{
    private readonly ScanChoiceViewModel _owner;
    private bool _isChosen;

    public ScanChoiceRow(ScanChoiceViewModel owner, ICleanupProvider provider)
    {
        _owner = owner;
        Provider = provider;
    }

    public ICleanupProvider Provider { get; }

    public string Name => Provider.Name;

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
}
