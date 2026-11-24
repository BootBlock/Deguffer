using CommunityToolkit.Mvvm.ComponentModel;

namespace Deguffer.App.ViewModels;

/// <summary>
/// Whether the last change made through one settings row could not be written to disk, so that the
/// row says so beside the value that went back. A settings page that silently discards a choice is
/// worse than one that never offered it, and a notice at the top of the page is silent to someone
/// changing a value several screens below it: they see the value revert and no reason.
///
/// <para><see cref="LatestSave"/> decides which row's is shown.</para>
/// </summary>
public sealed partial class SettingSave : ObservableObject
{
    [ObservableProperty]
    public partial bool Failed { get; internal set; }
}
