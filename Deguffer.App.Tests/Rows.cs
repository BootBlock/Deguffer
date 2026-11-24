using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Scanning;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// Steps, findings and rows for the Storage page's view-models, built from invented paths under a fake
/// profile. Nothing here touches the disk: a test that needs a tree builds it through
/// <see cref="StoragePage"/>.
/// </summary>
internal static class Rows
{
    /// <summary>A folder step of <paramref name="bytes"/>, which can be kept where it is given a <paramref name="key"/>.</summary>
    public static DeleteDirectoryStep Folder(
        string name,
        long bytes,
        bool requiresElevation = false,
        string? key = null,
        string? group = null) =>
        new($@"C:\Users\testuser\AppData\Local\Fake\{name}", name)
        {
            Estimated = ScanSize.FromLengths(bytes),
            RequiresElevation = requiresElevation,
            Identity = key is null ? null : new ItemIdentity(key, name),
            Group = group,
        };

    public static Finding Found(FakeCleanupProvider provider, params CleanupStep[] steps) =>
        new(provider, IsPresent: true, new CleanupPlan
        {
            ProviderId = provider.Id,
            ProviderName = provider.Name,
            Tier = provider.Tier,
            WhatHappensOnNextUse = provider.WhatHappensOnNextUse,
            Steps = steps,
        });

    public static SelectionMemory NothingRemembered() => new(new Dictionary<string, RememberedSelection>());

    /// <summary>A row as the page builds one, remembering nothing and keeping nothing.</summary>
    public static FindingViewModel Row(Finding finding, bool isElevated = false, KeepList? keeps = null) =>
        new(finding, NothingRemembered(), keeps ?? KeepList.Empty, isElevated);

    /// <summary>
    /// Tick or clear every step the row can tick, as one change. This is what a click on the row's
    /// checkbox does to a row that is clear or ticked; a test about the click itself drives
    /// <see cref="FindingViewModel.ToggleCommand"/>.
    /// </summary>
    public static void SetTicked(this FindingViewModel row, bool ticked) => row.SetSelected(row.Steps, ticked);

    /// <summary>Every <see cref="FindingViewModel.SelectionChanged"/> the row raises from here on.</summary>
    public static List<FindingViewModel> SelectionEvents(this FindingViewModel row)
    {
        var raised = new List<FindingViewModel>();
        row.SelectionChanged += raised.Add;
        return raised;
    }

    /// <summary>Every property the object announces from here on, by name, in order.</summary>
    public static List<string?> Notifications(this System.ComponentModel.INotifyPropertyChanged source)
    {
        var raised = new List<string?>();
        source.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }
}
