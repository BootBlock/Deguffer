using System.ComponentModel;
using Deguffer.App.ViewModels;
using Deguffer.Core.Execution;

namespace Deguffer.App.Tests;

/// <summary>
/// Whether a status line says anything, which a page binds to take the line out of its layout while
/// it says nothing, and is told again each time the line changes.
/// </summary>
public sealed class StatusLineViewModelTests
{
    [Fact]
    public void ALineWithASentenceIsShownAndAnEmptyOneIsNot()
    {
        var status = new StatusLineViewModel(StatusLine.Said("Scanning C:\\…"));
        var raised = new List<string?>();
        ((INotifyPropertyChanged)status).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(status.HasSentence);

        status.Say(StatusLine.Empty);

        Assert.False(status.HasSentence);
        Assert.Contains(nameof(StatusLineViewModel.HasSentence), raised);

        raised.Clear();
        status.Say(StatusLine.Refused("Deguffer does not scan this drive."));

        Assert.True(status.HasSentence);
        Assert.Contains(nameof(StatusLineViewModel.HasSentence), raised);
    }
}
