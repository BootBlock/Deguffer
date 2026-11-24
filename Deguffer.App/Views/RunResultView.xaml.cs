using System.ComponentModel;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.Views;

/// <summary>
/// The last clean's verdict and the line each location gets under it. See the XAML for why the lines
/// are folded and when they open.
/// </summary>
public sealed partial class RunResultView : UserControl
{
    public static readonly DependencyProperty ResultProperty =
        DependencyProperty.Register(nameof(Result), typeof(RunResultViewModel), typeof(RunResultView), new PropertyMetadata(null, OnResultChanged));

    public RunResultView()
    {
        InitializeComponent();
        ListAnimation.Play(RowsList, SystemMotion.Current);
    }

    /// <summary>Set once, by the Storage page, as it binds.</summary>
    public RunResultViewModel? Result
    {
        get => (RunResultViewModel?)GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }

    private static void OnResultChanged(DependencyObject sender, DependencyPropertyChangedEventArgs change)
    {
        var view = (RunResultView)sender;

        if (change.OldValue is RunResultViewModel old)
        {
            old.PropertyChanged -= view.OnResultPropertyChanged;
        }

        if (change.NewValue is RunResultViewModel now)
        {
            now.PropertyChanged += view.OnResultPropertyChanged;
        }
    }

    // A new run's card must not say an earlier run's report was copied.
    private void OnResultPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunResultViewModel.Statement))
        {
            CopyRunDiagnosticsResult.Text = string.Empty;
        }
    }

    /// <summary>
    /// Put the last run's report on the clipboard, say beside the link whether it went, and announce
    /// it, because a screen reader otherwise hears nothing happen when the link is pressed. It stays
    /// on the clipboard after Deguffer closes, which is when a user filing an issue is most likely to
    /// paste it.
    /// </summary>
    private async void OnCopyRunDiagnostics(object sender, RoutedEventArgs e)
    {
        if (Result is not { } result)
        {
            return;
        }

        var said = await ClipboardText.PutAsync(result.Diagnostics, CancellationToken.None) ?? "Copied. Paste it into a GitHub issue.";

        CopyRunDiagnosticsResult.Text = said;
        FrameworkElementAutomationPeer.FromElement(CopyRunDiagnosticsLink)?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent,
            said,
            "CopyRunDiagnostics");
    }
}
