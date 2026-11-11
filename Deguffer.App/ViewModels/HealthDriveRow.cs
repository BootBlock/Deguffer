using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Health;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// One drive on the Health page. Written over in place when a reading arrives, so the list keeps its
/// selection and the reader keeps their place (G4).
/// </summary>
public sealed partial class HealthDriveRow : ObservableObject
{
    public HealthDriveRow(DriveDescription drive)
    {
        Key = drive.Key;
        Title = drive.Name;
    }

    public DriveKey Key { get; }

    /// <summary>The last reading of this drive, or null before the first has arrived.</summary>
    public DriveHealth? Report { get; private set; }

    public ObservableCollection<string> Findings { get; } = [];

    public ObservableCollection<DriveFact> Facts { get; } = [];

    public ObservableCollection<HealthAttribute> Attributes { get; } = [];

    [ObservableProperty]
    public partial string Title { get; private set; }

    [ObservableProperty]
    public partial DriveVerdict Verdict { get; private set; } = DriveVerdict.Unknown;

    /// <summary>The chip's word, so the verdict never depends on its colour (§6.5).</summary>
    [ObservableProperty]
    public partial string VerdictText { get; private set; } = "Reading…";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "Reading this drive…";

    [ObservableProperty]
    public partial string Temperature { get; private set; } = string.Empty;

    /// <summary>The volumes on this drive, as a line.</summary>
    [ObservableProperty]
    public partial string Volumes { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasFindings { get; private set; }

    /// <summary>Follow the name a fresh listing gives, keeping everything the last reading showed.</summary>
    public void Show(DriveDescription drive) => Title = drive.Name;

    public void Show(DriveVolumes volumes) => Volumes = volumes.Line;

    public void Show(DriveHealth report)
    {
        Report = report;
        Title = report.Name;
        Verdict = report.Verdict;
        VerdictText = report.Verdict.Word();
        Summary = report.Summary;
        Temperature = report.Temperature ?? string.Empty;

        LiveList.Rewrite(Findings, report.Findings);
        LiveList.Show(Facts, report.Facts, fact => fact.Label);
        LiveList.Show(Attributes, report.Attributes, attribute => attribute.Key);

        HasFindings = Findings.Count > 0;
    }
}
