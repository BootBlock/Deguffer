using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// A clean's safety check on the Storage page's result card. What it shows is
/// <see cref="SafetyCheckViewModel"/>'s, and what it says is <see cref="RunOutcome"/>'s.
/// </summary>
public sealed partial class SafetyCheckView : UserControl
{
    public static readonly DependencyProperty ReportProperty =
        DependencyProperty.Register(nameof(Report), typeof(SafetyCheckViewModel), typeof(SafetyCheckView), new PropertyMetadata(null));

    public SafetyCheckView() => InitializeComponent();

    /// <summary>The page's safety check, which this control draws.</summary>
    public SafetyCheckViewModel? Report
    {
        get => (SafetyCheckViewModel?)GetValue(ReportProperty);
        set => SetValue(ReportProperty, value);
    }

    /// <inheritdoc cref="RunOutcome.WhatIsChecked" />
    public string WhatIsChecked => RunOutcome.WhatIsChecked;

    /// <summary>
    /// Put the last run's report on the clipboard, say on the card whether it went, and announce it,
    /// because a screen reader otherwise hears nothing happen. It stays on the clipboard after
    /// Deguffer closes, which is when a user filing an issue is most likely to paste it.
    /// </summary>
    private async void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        if (Report is not { } report)
        {
            return;
        }

        report.Copied(await ClipboardText.PutAsync(report.Diagnostics, CancellationToken.None));

        // The menu item has gone with its menu, so the button that opened it speaks for it.
        UIElement speaker = report.OffersDiagnosticsLink ? CopyRunDiagnosticsLink : SafetyCheckMenuButton;

        FrameworkElementAutomationPeer.FromElement(speaker)?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent,
            report.CopyResult,
            "CopyRunDiagnostics");
    }
}
