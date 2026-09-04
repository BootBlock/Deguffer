using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Deguffer.App.Views;

public sealed partial class CleanPage : Page
{
    /// <summary>
    /// How much of the window the row information dialog takes, in each direction. Large enough for
    /// a plan to be read as a list rather than through a slot, and small enough that the page it
    /// came from is still visible around it.
    /// </summary>
    private const double ShareOfWindow = 0.75;

    public CleanPage()
    {
        // Assigned before InitializeComponent so no x:Bind can ever evaluate against a null
        // view-model, whatever the framework's initialisation order does next.
        ViewModel = new CleanViewModel(
            CleanupPlanner.CreateDefault(),
            UserEnvironment.Current,
            App.Selections,
            () => new ContentDialogConfirmationPrompt(XamlRoot, ActualTheme));
        ViewModel.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();
        InitializeComponent();

        // Read once, here, and never again. Re-reading it whenever the page came back on screen
        // undid a choice whose write to disk had failed: PreferenceService leaves Current at the
        // old value on a failed save, so a trip to Settings and back put the list and the selector
        // silently back to Standard, in the same session and with no explanation.
        ShowFindingsAt(App.Preferences.Current.View);
        ListNotInstalled(App.Preferences.Current.ShowNotInstalled);

        // A scan and its results outlive a trip to Settings; rebuilding the page on the way back
        // would throw away a preview the user has not acted on yet.
        NavigationCacheMode = NavigationCacheMode.Required;

        // Bound to the page being on screen rather than to its construction: the preference only
        // governs a clean started from here, and a subscription to a process-lifetime static event
        // would otherwise root this page and its findings for good if the frame ever rebuilt it.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        if (ElevatedRelaunch.Requested is PreviewRequest)
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
    /// view-model stands it down when §7 will ask about the selection anyway — and with the typed
    /// phrase switched off, Tier 3 is one of the rows it therefore covers.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyPreferences();
        App.Preferences.Changed += OnPreferencesChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        App.Preferences.Changed -= OnPreferencesChanged;

    private void OnPreferencesChanged(object? sender, EventArgs e) => ApplyPreferences();

    /// <summary>
    /// Everything the Settings page can change under this one, pushed across together.
    ///
    /// Run on both edges deliberately. The subscription only exists while the page is on screen, so
    /// a change made in Settings arrives on the way back rather than as it is made — which is the
    /// same moment either way, because this page is the only thing that draws the result.
    /// </summary>
    private void ApplyPreferences()
    {
        ApplyRunPreferences();
        ApplyListPreferences();
    }

    /// <summary>
    /// Which rows the list draws, for the filters whose control lives on the Settings page.
    ///
    /// Re-read on every visit, unlike the view and the not-installed filter above. Those two are
    /// set from this page and applied before they are persisted, so re-reading them would undo a
    /// choice whose write to disk had failed. This one is set on a page that persists first and
    /// applies second, so <see cref="PreferenceService.Current"/> is never anything but what took
    /// effect.
    /// </summary>
    private void ApplyListPreferences() =>
        ViewModel.ShowAlreadyClear = App.Preferences.Current.ShowAlreadyClear;

    /// <summary>
    /// Draw the list at <paramref name="density"/>, and leave the selector agreeing with what is on
    /// screen.
    ///
    /// Safe to call from the selector's own change handler. The first call moves the index off -1
    /// and does re-enter that handler once, which lands back here with the index it now holds, and
    /// assigning an unchanged index raises nothing further.
    /// </summary>
    private void ShowFindingsAt(ViewDensity density)
    {
        ViewSelector.SelectedIndex = (int)density;
        FindingsList.ItemTemplate = (DataTemplate)Resources[
            density == ViewDensity.Compact ? "CompactFinding" : "StandardFinding"];
    }

    /// <summary>
    /// List the providers this machine does not have, or stop listing them, and leave the toggle
    /// agreeing with what is on screen. Read once at construction for the same reason the view is,
    /// and set on both sides here so neither can be the only one that knows.
    /// </summary>
    private void ListNotInstalled(bool show)
    {
        NotInstalledToggle.IsChecked = show;
        ViewModel.ShowNotInstalled = show;
    }

    /// <summary>
    /// Applied first and persisted second, exactly as the view is, and for the same reason: it
    /// decides what is on screen and nothing about what gets deleted.
    /// </summary>
    private void OnShowNotInstalledChanged(object sender, RoutedEventArgs e)
    {
        var show = NotInstalledToggle.IsChecked == true;

        ListNotInstalled(show);
        App.Preferences.Update(current => current with { ShowNotInstalled = show });
    }

    /// <summary>
    /// The view is applied first and persisted second, so it takes effect whether or not the
    /// preferences file can be written. That inverts <see cref="PreferenceService"/>'s usual order
    /// deliberately: it holds that order so a rejected write cannot take effect for the session,
    /// which is right for a setting that governs what gets deleted and wrong for one that governs
    /// how tall a row is.
    ///
    /// A failed write therefore costs the user this choice at the next launch, and nothing sooner,
    /// because the stored value is read once at construction and never re-read. The result is
    /// discarded rather than reported: the info bar on this page carries §5.6's verification
    /// headline, which is not a thing to displace for a setting the user can see took effect.
    /// </summary>
    private void OnViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var density = (ViewDensity)ViewSelector.SelectedIndex;

        ShowFindingsAt(density);
        App.Preferences.Update(current => current with { View = density });
    }

    /// <summary>
    /// Every preference that changes what a run does, pushed across together.
    ///
    /// The two confirmation settings are one decision from the user's side — what gets asked before
    /// a deletion — and applying only one of them here is how a Tier 3 row would come to be asked
    /// about by neither. The guard on recently changed files joins them because it is applied at
    /// the same two moments and would go just as silently stale on its own: it is read when the
    /// next preview runs, so a change made in Settings takes effect from that preview onwards
    /// rather than retrospectively.
    /// </summary>
    private void ApplyRunPreferences()
    {
        var preferences = App.Preferences.Current;

        ViewModel.ConfirmCleanAsync = preferences.ConfirmBeforeCleaning ? ConfirmCleanAsync : null;
        ViewModel.RequireTypedConfirmation = preferences.RequireTypedConfirmation;
        ViewModel.KeepFilesChangedWithinHours = preferences.KeepFilesChangedWithinHours;
    }

    private async Task<bool> ConfirmCleanAsync(CleanConfirmation confirmation)
    {
        var dialog = new ContentDialog
        {
            // A dialog built in code inherits no window; without the page's XamlRoot it has
            // nowhere to open.
            XamlRoot = XamlRoot,

            // It opens in the popup layer rather than inside this page, so it does not inherit the
            // theme applied to the window root — without this it renders dark over a light window.
            RequestedTheme = ActualTheme,

            // "Caches" holds only while everything listed rebuilds itself. A user who switches the
            // typed phrase off sends Tier 3 here as well, and a Recycle Bin called a cache in the
            // title of the dialog that authorises deleting it permanently is the same understatement
            // the body already refuses to make.
            Title = confirmation.AllRegenerable ? "Clean these caches?" : "Delete these items?",
            Content = new CleanConfirmationView(confirmation),
            PrimaryButtonText = "Clean",
            CloseButtonText = "Cancel",

            // The safe option is the default: this is the last point at which an accidental
            // selection can still be caught.
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Open the row's own explanation: whose folder it is, what it is for, whether it is worth
    /// cleaning, and the plan §7 requires to be inspectable before anything is deleted.
    ///
    /// The row arrives on the link's Tag rather than through its DataContext, which is how the
    /// Explore page's breadcrumbs already hand a template's item to its page.
    /// </summary>
    private async void OnWhatIsThisClicked(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { Tag: FindingViewModel finding })
        {
            await ShowProviderInfoAsync(finding);
        }
    }

    private async Task ShowProviderInfoAsync(FindingViewModel finding)
    {
        var dialog = new ContentDialog
        {
            // A dialog built in code inherits no window; without the page's XamlRoot it has
            // nowhere to open.
            XamlRoot = XamlRoot,

            // It opens in the popup layer rather than inside this page, so it does not inherit the
            // theme applied to the window root — without this it renders dark over a light window.
            RequestedTheme = ActualTheme,

            Title = finding.Name,
            Content = new ProviderInfoView(finding),
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,

            // The pivot has to fill what the sizing below gives it, and a ContentDialog centres
            // its content at its natural size unless told otherwise.
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };

        // Three quarters of the window, in both directions. The dialog's template sizes itself from
        // these four theme resources, and pinning each pair to the same number is what turns a
        // maximum into an exact size — a dialog left to its own 548px maximum would put a plan with
        // one step per workspace in a column narrower than the page it came from.
        //
        // Resolved against the dialog's own dictionary rather than the application's, so nothing
        // else that opens later inherits this one's dimensions.
        var width = XamlRoot.Size.Width * ShareOfWindow;
        var height = XamlRoot.Size.Height * ShareOfWindow;

        dialog.Resources["ContentDialogMinWidth"] = width;
        dialog.Resources["ContentDialogMaxWidth"] = width;
        dialog.Resources["ContentDialogMinHeight"] = height;
        dialog.Resources["ContentDialogMaxHeight"] = height;

        await dialog.ShowAsync();
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
