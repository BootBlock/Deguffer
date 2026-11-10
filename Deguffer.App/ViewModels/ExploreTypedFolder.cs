using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The box a location is typed into, most often a share the system picker has not found, and the
/// check that it names a folder before the page is pointed at it.
///
/// <para>Separate from <see cref="ExploreTargeting"/>, which it points once the check passes,
/// because it has state of its own the target does not: the text, the check under way, and why the
/// last one failed. What counts as a path and what the reader is told are Core's
/// (<see cref="TypedFolder"/>).</para>
/// </summary>
public sealed partial class ExploreTypedFolder : ObservableObject
{
    private readonly IPathProbe _probe;
    private readonly Action<string> _scopeTo;

    /// <param name="probe">Asks whether the typed folder is there, off the UI thread.</param>
    /// <param name="scopeTo">Points the page at a folder the check passed.</param>
    internal ExploreTypedFolder(IPathProbe probe, Action<string> scopeTo)
    {
        _probe = probe;
        _scopeTo = scopeTo;
    }

    /// <summary>Raised once the page points at what was typed, so the box can close.</summary>
    public event EventHandler? Accepted;

    /// <summary>What has been typed.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoCommand))]
    public partial string Text { get; set; } = string.Empty;

    /// <summary>Why the last location typed was not used, or null where nothing is wrong.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefusal))]
    public partial string? Refusal { get; private set; }

    public bool HasRefusal => Refusal is not null;

    /// <summary>
    /// Whether the page may be pointed somewhere else, which it may not while a scan runs. Set by the
    /// page from the same gate the drive box and the folder button follow.
    /// </summary>
    public bool CanRetarget
    {
        get;
        set
        {
            field = value;
            GoCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Check what was typed and, where it names a folder, point the page at it.
    ///
    /// <para>The text is read before anything is asked, because a reader who goes on typing while a
    /// server is asked has not changed what was asked about. The probe runs off the UI thread: on a
    /// share it waits on the server, for as long as the redirector waits for one that does not
    /// answer. Cancelled, it points nothing and says nothing, because nothing was found out.</para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGo), IncludeCancelCommand = true)]
    private async Task GoAsync(CancellationToken ct)
    {
        Refusal = null;

        if (TypedFolder.Folder(Text) is not { } folder)
        {
            Refusal = TypedFolder.NotAPath;

            return;
        }

        var presence = await Task.Run(() => _probe.ProbeDirectory(LongPath.Extended(folder), out _), ct).WaitAsync(ct);

        if (TypedFolder.Refusal(folder, presence) is { } refusal)
        {
            Refusal = refusal;

            return;
        }

        _scopeTo(folder);
        Accepted?.Invoke(this, EventArgs.Empty);
    }

    private bool CanGo() => CanRetarget && !string.IsNullOrWhiteSpace(Text);
}
