using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.App.Shell;
using Deguffer.Core.Startup;
using Microsoft.UI.Xaml.Controls;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Startup row menu's hand-offs to Windows (§7.5): show the program's file, show a Startup folder
/// entry's item, open the program's properties, and copy what the entry runs. None of them changes
/// anything. What each hands over, or why it cannot, is <see cref="StartupFiles"/>' decision. The
/// menu's one change is the page's own <see cref="StartupViewModel.ChangeCommand"/>.
/// </summary>
public sealed partial class StartupRowMenu : ObservableObject
{
    private readonly Func<StartupSource, string?> _folderOf;
    private readonly ShellHandoff _shell;
    private readonly Action<string, InfoBarSeverity> _say;

    /// <param name="folderOf">Where a Startup folder is, or null where Windows would not say.</param>
    /// <param name="shell">The hand-offs themselves.</param>
    /// <param name="say">Puts a sentence on the page: a refusal, or what Windows answered when a hand-off failed.</param>
    public StartupRowMenu(Func<StartupSource, string?> folderOf, ShellHandoff shell, Action<string, InfoBarSeverity> say)
    {
        _folderOf = folderOf;
        _shell = shell;
        _say = say;
    }

    /// <summary>The row the menu acts on, which is always the row the user picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItem))]
    [NotifyCanExecuteChangedFor(nameof(RevealProgramCommand), nameof(RevealItemCommand), nameof(PropertiesCommand), nameof(CopyCommand))]
    public partial StartupRow? Row { get; set; }

    /// <summary>Whether the row has an item in a Startup folder to show, as <see cref="StartupFiles.HasItem"/> decides.</summary>
    public bool HasItem => Row is { } row && StartupFiles.HasItem(row.Entry);

    private bool HasRow() => Row is not null;

    [RelayCommand(CanExecute = nameof(HasRow))]
    private void RevealProgram() => HandOff(entry => StartupFiles.Program(entry), _shell.Reveal);

    [RelayCommand(CanExecute = nameof(HasRow))]
    private void RevealItem() => HandOff(entry => StartupFiles.Item(entry, _folderOf(entry.Key.Source)), _shell.Reveal);

    [RelayCommand(CanExecute = nameof(HasRow))]
    private void Properties() => HandOff(entry => StartupFiles.Program(entry), _shell.Properties);

    [RelayCommand(CanExecute = nameof(HasRow))]
    private void Copy() => HandOff(entry => StartupFiles.Command(entry), _shell.Copy);

    /// <summary>
    /// Hand <paramref name="decide"/>'s answer for the row to <paramref name="to"/>, or say why not.
    /// Nothing happens without a row, or for an entry Core offers nothing of this kind for.
    /// </summary>
    private void HandOff(Func<StartupEntry, StartupHandoff?> decide, Func<string, string?> to)
    {
        if (Row is not { } row || decide(row.Entry) is not { } handoff)
        {
            return;
        }

        if (handoff.Value is { } value)
        {
            if (to(value) is { } failed)
            {
                _say(failed, InfoBarSeverity.Warning);
            }
        }
        else if (handoff.Refusal is { } refusal)
        {
            _say(refusal, InfoBarSeverity.Informational);
        }
    }
}
