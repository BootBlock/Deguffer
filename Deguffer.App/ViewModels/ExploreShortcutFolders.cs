using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Execution;
using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Exploring.Links;
using Deguffer.Core.Viewing;
using Deguffer.Core.Working;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Explore page's panel of broken shortcuts and links in the Start menu, on the desktop and in the
/// Startup folders, read from those folders rather than from the scan on screen. See
/// <see cref="ShortcutFolderReport"/>.
///
/// <para><b>§7.1, by path rather than by node.</b> Nothing here is selected or acted on but the one row
/// whose button was pressed. Its removal is Explore's own: <see cref="ExploreActions"/> asks the policy,
/// asks the user, goes to the Recycle Bin and asserts §5.6, and the link itself goes, never what it
/// led to. Only a link whose target is gone is listed, and its target is asked about again after the
/// user agrees, because a drive plugged in meanwhile has put it back.</para>
/// </summary>
public sealed partial class ExploreShortcutFolders : ObservableObject
{
    /// <summary>What a row withdrawn by the last look says, in the report of its removal.</summary>
    internal const string Withdrawn = "What it points at is back, or it points somewhere else now, so it was left where it is.";

    private readonly ExploreActions _actions;
    private readonly Func<LinkInspection> _inspect;
    private readonly IReadOnlyList<string> _folders;
    private readonly BusyWork _work;

    /// <summary>The inspection the rows on screen were read with, which a removal asks again.</summary>
    private LinkInspection? _inspection;

    /// <summary>The reading in flight, cancelled when another replaces it or the panel closes.</summary>
    private CancellationTokenSource? _reading;

    /// <param name="inspect">A new inspection, with nothing remembered, for each reading.</param>
    /// <param name="folders">From <see cref="ShortcutFolders.Of"/>.</param>
    public ExploreShortcutFolders(
        ExploreActions actions, Func<LinkInspection> inspect, IReadOnlyList<string> folders, BusyWork work)
    {
        _actions = actions;
        _inspect = inspect;
        _folders = folders;
        _work = work;
    }

    /// <summary>The broken links, by path. Updated in place, so the panel keeps its scroll position (G4).</summary>
    public ObservableCollection<ShortcutFolderRow> Rows { get; } = [];

    /// <summary>What the reading found, or that it is reading.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>
    /// Whether the panel is on screen. The folders are read each time it opens, because a program
    /// uninstalled since the last look has left its shortcuts behind since then.
    /// </summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Whether a removal is running, which no second one may start beside.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial bool IsRemoving { get; private set; }

    /// <summary>Raised with what a removal did, for the page's status line.</summary>
    public event EventHandler<StatusLine>? Reported;

    /// <summary>
    /// Raised with the report of a removal that took something, so the page can draw the tree on
    /// screen without it where the tree holds it.
    /// </summary>
    public event EventHandler<ExploreRemovalReport>? Removed;

    partial void OnIsActiveChanged(bool value)
    {
        if (value)
        {
            _ = ReadAsync();
        }
        else
        {
            _reading?.Cancel();
            _reading = null;
        }
    }

    /// <summary>Read the folders again.</summary>
    [RelayCommand]
    private Task RefreshAsync() => ReadAsync();

    [RelayCommand]
    private void Reveal(ShortcutFolderRow? row)
    {
        if (row is not null && ShellActions.Reveal(row.Path, isDirectory: false) is { } failure)
        {
            Reported?.Invoke(this, StatusLine.Said(failure));
        }
    }

    /// <summary>
    /// §7.1's default removal, to the Recycle Bin, of the one row whose button was pressed. Never
    /// outright: the issue this panel answers asked for the bin, and a shortcut is the case recovery
    /// is cheapest for.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync(ShortcutFolderRow? row)
    {
        if (row is null || _inspection is not { } inspection)
        {
            return;
        }

        IsRemoving = true;
        var work = _work.Begin();

        try
        {
            var link = row.Link;
            var report = await _actions.RemoveAsync(
                [new ExploreItem(link.Path, link.IsDirectory, link.Bytes, LowerBound: false)],
                ExploreRemovalMode.RecycleBin,
                lastWord: _ => inspection.IsStillGone(link) ? null : Withdrawn);

            if (report is null)
            {
                Reported?.Invoke(this, StatusLine.Said("Nothing was removed."));
                return;
            }

            Reported?.Invoke(this, report.Statement);

            // A check left unpassed is §5.6 failing to prove the removal left what it had to.
            if (report.Statement.Checks.Count > 0)
            {
                work.Fail();
            }

            if (report.Removed.Count > 0)
            {
                Removed?.Invoke(this, report);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // For the reason ExploreSelection gives: escaping an async command takes the process down.
            Reported?.Invoke(this, StatusLine.Said($"The removal stopped: {ex.Message}"));
            work.Fail();
        }
        finally
        {
            IsRemoving = false;
            work.Dispose();
        }

        await ReadAsync();
    }

    private bool CanRemove(ShortcutFolderRow? row) => !IsRemoving && row is not null;

    /// <summary>Read every folder off the window's thread, and show what was found if nothing replaced it.</summary>
    private async Task ReadAsync()
    {
        _reading?.Cancel();

        var reading = new CancellationTokenSource();
        _reading = reading;

        var inspection = _inspect();
        Summary = "Reading the Start menu, the desktop and the Startup folders…";

        try
        {
            var report = await Task.Run(() => ShortcutFolderReport.Read(_folders, inspection, reading.Token), reading.Token);

            if (!ReferenceEquals(_reading, reading))
            {
                return;
            }

            _inspection = inspection;
            LiveList.Show(Rows, [.. report.Links.Select(link => new ShortcutFolderRow(link, RemoveCommand, RevealCommand))], row => row.Key);
            Summary = SummaryOf(report);
        }
        catch (OperationCanceledException)
        {
            // Replaced, or the panel closed. Whatever replaced it says what it found.
        }
    }

    /// <summary>
    /// How many links lead nowhere, how many more could not be checked, and which folders Windows would
    /// not list, so a short panel says it is short rather than reading as a tidy Start menu.
    /// </summary>
    internal static string SummaryOf(ShortcutFolderReport report)
    {
        var sentence = report.Links.Count switch
        {
            0 => "No shortcut or link there leads nowhere.",
            1 => "1 shortcut or link there leads nowhere.",
            var count => $"{count:N0} shortcuts and links there lead nowhere.",
        };

        sentence = report.Unreachable switch
        {
            0 => sentence,
            1 => $"{sentence} 1 more leads to a drive or share that is not connected or could not be checked, so it is not listed.",
            var count => $"{sentence} {count:N0} more lead to a drive or share that is not connected or could not be checked, so they are not listed.",
        };

        return report.Unread switch
        {
            [] => sentence,
            [var only] => $"{sentence} Windows would not list {only}, so this may be short.",
            var many => $"{sentence} Windows would not list {many.Count} folders, so this may be short.",
        };
    }
}
