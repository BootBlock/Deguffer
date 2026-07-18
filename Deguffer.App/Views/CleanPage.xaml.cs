using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Deguffer.App.Views;

public sealed partial class CleanPage : Page
{
    public CleanPage()
    {
        // Assigned before InitializeComponent so no x:Bind can ever evaluate against a null
        // view-model, whatever the framework's initialisation order does next.
        ViewModel = new CleanViewModel(
            CleanupPlanner.CreateDefault(),
            UserEnvironment.Current,
            () => new ContentDialogConfirmationPrompt(XamlRoot, ActualTheme));
        ViewModel.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();
        InitializeComponent();

        // A scan and its results outlive a trip to Settings; rebuilding the page on the way back
        // would throw away a preview the user has not acted on yet.
        NavigationCacheMode = NavigationCacheMode.Required;

        // Bound to the page being on screen rather than to its construction: the preference only
        // governs a clean started from here, and a subscription to a process-lifetime static event
        // would otherwise root this page and its findings for good if the frame ever rebuilt it.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        if (ElevatedRelaunch.ShouldRescanOnLaunch)
        {
            // Deferred to Loaded rather than run here: planning posts rows back through the
            // dispatcher, and starting it before the page is live would report into nothing.
            Loaded += StartRequestedRescan;
        }
    }

    public CleanViewModel ViewModel { get; }

    /// <summary>
    /// The view-model asks whether to go ahead by calling the hook; whether it asks at all is the
    /// preference, expressed by leaving the hook unset. Deleting at these sizes has no undo (§8),
    /// so the default is to ask.
    ///
    /// This is the blanket confirmation, which covers the Tier 1 case §7 does not prompt for. The
    /// view-model stands it down when §7 will ask about the selection anyway.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyConfirmationPreference();
        App.Preferences.Changed += OnPreferencesChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        App.Preferences.Changed -= OnPreferencesChanged;

    private void OnPreferencesChanged(object? sender, EventArgs e) => ApplyConfirmationPreference();

    private void ApplyConfirmationPreference() =>
        ViewModel.ConfirmCleanAsync = App.Preferences.Current.ConfirmBeforeCleaning ? ConfirmCleanAsync : null;

    private async Task<bool> ConfirmCleanAsync(string summary)
    {
        var dialog = new ContentDialog
        {
            // A dialog built in code inherits no window; without the page's XamlRoot it has
            // nowhere to open.
            XamlRoot = XamlRoot,

            // It opens in the popup layer rather than inside this page, so it does not inherit the
            // theme applied to the window root — without this it renders dark over a light window.
            RequestedTheme = ActualTheme,

            Title = "Clean these caches?",
            Content = summary,
            PrimaryButtonText = "Clean",
            CloseButtonText = "Cancel",

            // The safe option is the default: this is the last point at which an accidental
            // selection can still be caught.
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void StartRequestedRescan(object sender, RoutedEventArgs e)
    {
        Loaded -= StartRequestedRescan;

        if (ViewModel.PreviewCommand.CanExecute(null))
        {
            ViewModel.PreviewCommand.Execute(null);
        }
    }
}
