using Deguffer.App.Shell;
using Deguffer.Core.Configuration;
using Deguffer.Testing;

namespace Deguffer.App.Tests;

/// <summary>
/// What <see cref="PreferenceService.Update"/> reports: a change written, a change that asked for what
/// already holds, and a write the folder refused, which leaves memory and disk agreeing.
/// </summary>
public sealed class PreferenceServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public PreferenceServiceTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AChangeIsSavedAndHolds()
    {
        var preferences = new PreferenceService(new PreferenceStore(_environment));

        Assert.Equal(SaveOutcome.Saved, preferences.Update(current => current with { BackdropEnabled = false }));
        Assert.False(preferences.Current.BackdropEnabled);
        Assert.False(new PreferenceStore(_environment).Load().BackdropEnabled);
    }

    /// <summary>
    /// Asking for what already holds is not a save: it is what a control sends when it is put back after
    /// a refused write, and read as one it told the page the folder had just taken a write.
    /// </summary>
    [Fact]
    public void AChangeToWhatAlreadyHoldsIsUnchangedEvenWhereTheFolderRefusesWrites()
    {
        var preferences = new PreferenceService(new PreferenceStore(_environment));
        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "Deguffer", "preferences.json"));
        var raised = 0;
        preferences.Changed += (_, _) => raised++;

        Assert.Equal(SaveOutcome.Unchanged, preferences.Update(current => current with { BackdropEnabled = current.BackdropEnabled }));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void ARefusedWriteChangesNothing()
    {
        var preferences = new PreferenceService(new PreferenceStore(_environment));
        Directory.CreateDirectory(Path.Combine(_environment.LocalAppData, "Deguffer", "preferences.json"));

        Assert.Equal(SaveOutcome.Refused, preferences.Update(current => current with { BackdropEnabled = false }));
        Assert.True(preferences.Current.BackdropEnabled);
    }
}
