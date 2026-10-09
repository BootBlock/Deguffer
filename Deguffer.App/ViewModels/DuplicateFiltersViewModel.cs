using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Core.Duplicates;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Duplicates page's criteria, checksum and filters (§7.4), each stored as a preference the
/// moment it changes. What the values mean for a search is <see cref="DuplicatePreferences"/>'s:
/// this maps them to what the controls expose and writes each back.
/// </summary>
public sealed partial class DuplicateFiltersViewModel : ObservableObject
{
    private readonly PreferenceService _preferences;

    public DuplicateFiltersViewModel(PreferenceService preferences)
    {
        _preferences = preferences;
    }

    /// <summary>Raised after a value was stored, so the page can say again whether it can search.</summary>
    public event EventHandler? Changed;

    /// <summary>What is stored now, which a search is built from.</summary>
    public DuplicatePreferences Current => _preferences.Current.Duplicates;

    /// <summary>The checksums this machine offers, by the names other tools print them under, in the order Core lists them.</summary>
    public IReadOnlyList<string> AlgorithmNames { get; } = [.. ChecksumAlgorithms.Offered.Select(algorithm => algorithm.Name())];

    /// <summary>The units a size is written in, ordered to match <see cref="SizeUnit"/>.</summary>
    public IReadOnlyList<string> UnitNames { get; } = [.. Enum.GetValues<SizeUnit>().Select(unit => unit.ToString())];

    /// <summary>How the extension list is read, ordered to match <see cref="ExtensionFilterMode"/>.</summary>
    public IReadOnlyList<string> ExtensionModeNames { get; } = ["Every extension", "Only these extensions", "Every extension but these"];

    public bool MatchName
    {
        get => Current.Criteria.HasFlag(MatchCriteria.Name);
        set => Criterion(MatchCriteria.Name, value);
    }

    public bool MatchSize
    {
        get => Current.Criteria.HasFlag(MatchCriteria.Size);
        set => Criterion(MatchCriteria.Size, value);
    }

    public bool MatchModified
    {
        get => Current.Criteria.HasFlag(MatchCriteria.Modified);
        set => Criterion(MatchCriteria.Modified, value);
    }

    public bool MatchContent
    {
        get => Current.Criteria.HasFlag(MatchCriteria.Content);
        set
        {
            Criterion(MatchCriteria.Content, value);

            // The checksum is read only by a content match, so its box follows this one.
            OnPropertyChanged(nameof(AlgorithmIndex));
        }
    }

    /// <summary>
    /// Index into <see cref="AlgorithmNames"/>: the checksum a search will use, which is the default
    /// where the one stored is not offered here.
    /// </summary>
    public int AlgorithmIndex
    {
        get => IndexOf(Current.OfferedAlgorithm);
        set
        {
            // A combo box reports -1 while its items are replaced; that is no choice.
            if (value >= 0 && value < ChecksumAlgorithms.Offered.Count)
            {
                Apply(current => current with { Algorithm = ChecksumAlgorithms.Offered[value] });
            }
        }
    }

    /// <summary>The smallest size's amount, or <see cref="double.NaN"/> for none, which a <c>NumberBox</c> shows as empty.</summary>
    public double SmallestAmount
    {
        get => Current.Smallest.Amount ?? double.NaN;
        set => Apply(current => current with { Smallest = current.Smallest with { Amount = SizeLimit.Entered(value) } });
    }

    public int SmallestUnitIndex
    {
        get => (int)Current.Smallest.Unit;
        set => Apply(current => current with { Smallest = current.Smallest with { Unit = UnitAt(value, current.Smallest) } });
    }

    /// <summary>The largest size's amount, on the terms <see cref="SmallestAmount"/> states.</summary>
    public double LargestAmount
    {
        get => Current.Largest.Amount ?? double.NaN;
        set => Apply(current => current with { Largest = current.Largest with { Amount = SizeLimit.Entered(value) } });
    }

    public int LargestUnitIndex
    {
        get => (int)Current.Largest.Unit;
        set => Apply(current => current with { Largest = current.Largest with { Unit = UnitAt(value, current.Largest) } });
    }

    public int ExtensionModeIndex
    {
        get => (int)Current.ExtensionMode;
        set
        {
            if (Enum.IsDefined((ExtensionFilterMode)value))
            {
                Apply(current => current with { ExtensionMode = (ExtensionFilterMode)value });
                OnPropertyChanged(nameof(ReadsExtensions));
            }
        }
    }

    /// <summary>Whether the extension list is read, so its box is offered only then.</summary>
    public bool ReadsExtensions => Current.ExtensionMode != ExtensionFilterMode.Any;

    public string Extensions
    {
        get => Current.Extensions;
        set => Apply(current => current with { Extensions = value ?? string.Empty });
    }

    public bool SearchHidden
    {
        get => Current.SearchHidden;
        set => Apply(current => current with { SearchHidden = value });
    }

    public bool SearchSystem
    {
        get => Current.SearchSystem;
        set => Apply(current => current with { SearchSystem = value });
    }

    public bool SearchPassedOverPlaces
    {
        get => Current.SearchPassedOverPlaces;
        set => Apply(current => current with { SearchPassedOverPlaces = value });
    }

    /// <summary>Whether the last change could not be stored, so the page can say it will not last.</summary>
    [ObservableProperty]
    public partial bool SaveFailed { get; private set; }

    private static int IndexOf(ChecksumAlgorithm algorithm)
    {
        for (var i = 0; i < ChecksumAlgorithms.Offered.Count; i++)
        {
            if (ChecksumAlgorithms.Offered[i] == algorithm)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>The unit at <paramref name="index"/>, or the one there is where a combo box reports no choice.</summary>
    private static SizeUnit UnitAt(int index, SizeLimit current) =>
        Enum.IsDefined((SizeUnit)index) ? (SizeUnit)index : current.Unit;

    private void Criterion(MatchCriteria criterion, bool on, [CallerMemberName] string setting = "") =>
        Apply(current => current with { Criteria = on ? current.Criteria | criterion : current.Criteria & ~criterion }, setting);

    /// <param name="setting">The property whose control made the change, which is the setter calling this.</param>
    private void Apply(Func<DuplicatePreferences, DuplicatePreferences> change, [CallerMemberName] string setting = "")
    {
        SaveFailed = !_preferences.Update(current => current with { Duplicates = change(current.Duplicates) });

        // A rejected write puts every control back to what holds, and an accepted one reads back what
        // was stored.
        OnPropertyChanged(SaveFailed ? string.Empty : setting);

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
