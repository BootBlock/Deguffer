using System.ComponentModel;
using System.Runtime.InteropServices;
using Deguffer.App.Shell;
using Deguffer.App.ViewModels;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Deguffer.App.Views;

public sealed partial class CleanPage : Page
{
    /// <summary>
    /// How much of the window the row information dialog takes, in each direction. Large enough for
    /// a plan to be read as a list rather than through a slot, and small enough that the page it
    /// came from is still visible around it.
    /// </summary>
    private const double ShareOfWindow = 0.75;

    /// <summary>The row whose item list is on screen, so the keyboard can go back to it when the list closes.</summary>
    private FindingViewModel? _itemsShownFor;

    public CleanPage()
    {
        // Assigned before InitializeComponent so no x:Bind can ever evaluate against a null
        // view-model, whatever the framework's initialisation order does next.
        ViewModel = new CleanViewModel(
            CleanupPlanner.CreateDefault(App.Preferences, tuning: App.ScanTuning),
            UserEnvironment.Current,
            VolumeInventory.Current,
            ElevatedRelaunch.IsElevated,
            App.Selections,
            App.Keeps,
            App.Running,
            () => new ContentDialogConfirmationPrompt(XamlRoot, ActualTheme),
            AppVersion.Current,
            new WhenCompleteViewModel(App.Preferences, WindowsSession.Current));
        ViewModel.ReplacedByElevatedInstance += (_, _) => Application.Current.Exit();
        ViewModel.WhenComplete.ConfirmAsync = CountDownAsync;
        ViewModel.WhenComplete.ExitRequested += (_, _) => App.CloseWhenIdle();
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

        // The view-model is this page's own, so the subscription lives exactly as long as both do.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

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

        // Settings can release a kept item while this page is away, and a row still showing it as
        // kept would leave the user unable to tick something they have just asked to be offered.
        ViewModel.ApplyKeepList();
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

    /// <summary>
    /// The countdown before what follows a clean, over the whole window rather than this page: a
    /// clean goes on running while the user is on another page, and this page has no
    /// <see cref="XamlRoot"/> while it is off screen. Nothing is carried out where there is no window
    /// to warn the user in.
    /// </summary>
    private Task<bool> CountDownAsync(CompletionCountdown countdown) =>
        App.MainWindow?.Content is FrameworkElement { XamlRoot: { } root } window
            ? ContentDialogCompletionCountdown.AskAsync(countdown, App.Running, root, window.ActualTheme)
            : Task.FromResult(false);

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

        return await ModalDialog.ShowAsync(dialog) == ContentDialogResult.Primary;
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

    /// <summary>List the row's items in place of the rows. The row arrives on the link's Tag, as above.</summary>
    private void OnItemsLinkClicked(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { Tag: FindingViewModel finding })
        {
            ViewModel.ShowItems(finding);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanViewModel.ShownItems))
        {
            ShowItemsOrRows();
        }

        // A new run's card must not say an earlier run's report was copied.
        if (e.PropertyName == nameof(CleanViewModel.RunStatement))
        {
            CopyRunDiagnosticsResult.Text = string.Empty;
        }
    }

    /// <summary>
    /// Put the last run's report on the clipboard, and say on the card whether it went.
    ///
    /// <para>Flushed, so the report is still there to paste after Deguffer closes, which is when a
    /// user filing an issue is most likely to paste it.</para>
    /// </summary>
    /// <summary>CLIPBRD_E_CANT_OPEN: Windows' answer while another program holds the clipboard open.</summary>
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0);

    private void OnCopyRunDiagnostics(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(ViewModel.RunDiagnostics);

        try
        {
            Clipboard.SetContent(package);
            Clipboard.Flush();
            CopyRunDiagnosticsResult.Text = "Copied. Paste it into a GitHub issue.";
        }
        catch (COMException ex) when (ex.HResult == ClipboardCannotOpen)
        {
            // Another program has the clipboard open. It lets go within moments, so the answer is to
            // try again rather than to give up.
            CopyRunDiagnosticsResult.Text = "Another program is using the clipboard. Try again.";
        }
    }

    /// <summary>
    /// Put the item list the view-model names on the card, or take it off and give the keyboard back
    /// to the row it came from.
    ///
    /// <para>The list is built afresh each time rather than kept, because it belongs to one row of one
    /// preview. The rows' own list is collapsed rather than rebuilt while it is away, so it comes back
    /// scrolled to where the reader left it.</para>
    /// </summary>
    private void ShowItemsOrRows()
    {
        if (ViewModel.ShownItems is { } items)
        {
            _itemsShownFor = items.Row;
            ItemsHost.Content = new ItemListView(items, ViewModel.ToggleKeep, ViewModel.CloseItems);
            return;
        }

        ItemsHost.Content = null;

        if (_itemsShownFor is { } row)
        {
            _itemsShownFor = null;

            // Deferred, because the rows are only just visible again and a control that has not been
            // laid out cannot take focus. A preview that replaced the rows leaves nothing to find,
            // and that is correct: the row the list came from no longer exists.
            DispatcherQueue.TryEnqueue(() => FocusItemsLinkOf(row));
        }
    }

    private void FocusItemsLinkOf(FindingViewModel row)
    {
        if (FindingsList.ContainerFromItem(row) is DependencyObject container
            && ItemsLinkIn(container, row) is { } link)
        {
            link.Focus(FocusState.Programmatic);
        }
    }

    private static HyperlinkButton? ItemsLinkIn(DependencyObject parent, FindingViewModel row)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is HyperlinkButton { Tag: FindingViewModel tagged } link
                && ReferenceEquals(tagged, row)
                && AutomationProperties.GetName(link) == row.Text.ItemsLinkName)
            {
                return link;
            }

            if (ItemsLinkIn(child, row) is { } found)
            {
                return found;
            }
        }

        return null;
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
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,

            // The pivot has to fill what the sizing below gives it, and a ContentDialog centres
            // its content at its natural size unless told otherwise.
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };

        // Assigned once the dialog exists, because the content's way to the item list closes it.
        dialog.Content = new ProviderInfoView(finding, row =>
        {
            dialog.Hide();
            ViewModel.ShowItems(row);
        });

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

        await ModalDialog.ShowAsync(dialog);
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
