using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Deguffer.Core.Execution;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// A page's status line as the page binds it: the sentence, and the §5.6 checks listed beside it.
/// What the line says, where it came from and which checks go with it are <see cref="StatusLine"/>'s,
/// and this only shows it.
///
/// <para>One line is replaced as a whole, so a sentence and the checks beside it can never belong to
/// two different statements.</para>
/// </summary>
public sealed partial class StatusLineViewModel : ObservableObject
{
    /// <param name="first">What the line says before anything is said on it.</param>
    public StatusLineViewModel(StatusLine first) => Say(first);

    /// <summary>What the line holds now, which a page reads to decide what replaces it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Sentence))]
    [NotifyPropertyChangedFor(nameof(HasSentence))]
    public partial StatusLine Line { get; private set; } = StatusLine.Empty;

    /// <summary>What the line says.</summary>
    public string Sentence => Line.Sentence;

    /// <summary>
    /// Whether the line says anything, so a page can take it out of its layout when it does not:
    /// an empty text block still keeps a line's height under the content.
    /// </summary>
    public bool HasSentence => Line.Sentence.Length > 0;

    /// <summary>
    /// The checks the sentence counts as not passed, each by the path it is about and what it found.
    /// Updated in place rather than replaced, because the page binds the collection once.
    /// </summary>
    public ObservableCollection<VerificationCheck> Checks { get; } = [];

    /// <summary>Whether there is anything in that list, so the page shows it only when there is.</summary>
    public bool HasChecks => Checks.Count > 0;

    /// <summary>Put <paramref name="line"/> on the status line, with its checks beside it.</summary>
    public void Say(StatusLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        Line = line;
        LiveList.Rewrite(Checks, line.Checks);
        OnPropertyChanged(nameof(HasChecks));
    }
}
