using System.Security.Principal;
using Deguffer.Core.Safety;
using Deguffer.Testing;
using Microsoft.Win32;

namespace Deguffer.Core.Tests;

/// <summary>
/// Whether the account Deguffer runs as is the one signed in to Windows, which folders it owns, and
/// whose registry hive a setting is read from as a result.
/// </summary>
public sealed class RunningAccountTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public RunningAccountTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheSignedInUsersOwnProcessIsTheSignedInUserAndSaysNothing()
    {
        var account = RunningAccount.Of(_environment);

        Assert.Equal(AccountStanding.SignedInUser, account.Standing);
        Assert.Null(account.Notice);
    }

    /// <summary>
    /// Elevated with another account's credentials, the process's account and the session's differ, and
    /// the window names both.
    /// </summary>
    [Fact]
    public void ADifferentIdentifierIsAnotherAccountAndTheNoticeNamesBoth()
    {
        var account = RunningAccount.Of(_environment.ElevatedAsAnotherAccount());

        Assert.Equal(AccountStanding.AnotherAccount, account.Standing);
        Assert.Contains($@"{FakeUserEnvironment.Domain}\{FakeUserEnvironment.Account}", account.Notice, StringComparison.Ordinal);
        Assert.Contains(FakeUserEnvironment.SignedInName, account.Notice, StringComparison.Ordinal);
    }

    /// <summary>
    /// A domain account away from its domain has a name and no identifier, so the name decides, and an
    /// identifier that does not match is never overruled by a name that does.
    /// </summary>
    [Fact]
    public void TheNameDecidesOnlyWhereAnIdentifierIsMissing()
    {
        var own = $@"{FakeUserEnvironment.Domain}\{FakeUserEnvironment.Account}";

        _environment.WithSessionUser(new SignedInAccount(own.ToUpperInvariant(), SecurityIdentifier: null));
        Assert.Equal(AccountStanding.SignedInUser, RunningAccount.Of(_environment).Standing);

        _environment.WithSessionUser(new SignedInAccount(FakeUserEnvironment.SignedInName, SecurityIdentifier: null));
        Assert.Equal(AccountStanding.AnotherAccount, RunningAccount.Of(_environment).Standing);

        _environment.WithSessionUser(new SignedInAccount(own, FakeUserEnvironment.SignedInSecurityIdentifier));
        Assert.Equal(AccountStanding.AnotherAccount, RunningAccount.Of(_environment).Standing);
    }

    [Fact]
    public void NoSignedInUserIsUnconfirmedAndSaysSo()
    {
        var account = RunningAccount.Of(_environment.WithNoSessionUser());

        Assert.Equal(AccountStanding.Unconfirmed, account.Standing);
        Assert.Contains("Windows would not say who is signed in", account.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFoldersAreTheProfileItsDataTheTemporaryFolderAndEveryMovedKnownFolder()
    {
        var moved = Path.Combine(_temp.Path, "elsewhere", "Pictures");
        var folders = RunningAccount.Of(_environment.WithPersonalFolderAt(moved)).Folders;

        foreach (var expected in new[]
        {
            _environment.UserProfile,
            _environment.LocalAppData,
            _environment.RoamingAppData,
            _environment.TempPath,
            moved,
        })
        {
            Assert.Contains(expected, folders, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The signed-in user's identifier where a name lookup cannot give it, such as a domain account away
    /// from its domain: their loaded hive names them in its <c>Volatile Environment</c> key. Read from the
    /// real registry, because the key is Windows' own and this user is signed in.
    /// </summary>
    [Fact]
    public void ASignedInUsersLoadedHiveNamesTheirIdentifier()
    {
        using var session = Registry.CurrentUser.OpenSubKey("Volatile Environment")!;
        var user = (string)session.GetValue("USERNAME")!;
        var domain = (string)session.GetValue("USERDOMAIN")!;

        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, UserEnvironment.LoadedHiveOf(user, domain));
        Assert.Null(UserEnvironment.LoadedHiveOf(user + "-nobody", domain));
    }

    /// <summary>
    /// The real choice of hive. The signed-in user's own process reads <c>HKEY_CURRENT_USER</c> and has
    /// no second hive. Elevated as another account, a setting is read from the signed-in user's hive under
    /// <c>HKEY_USERS</c>, the running account's own is kept for a refusal, and where nobody can say who
    /// is signed in no setting is read at all.
    ///
    /// <para>The signed-in user stands in as the "other" account here, by its own identifier, because
    /// its hive is the one under <c>HKEY_USERS</c> a test can write to and read back.</para>
    /// </summary>
    [Fact]
    public void ASettingIsReadFromTheSignedInUsersHive()
    {
        using var scratch = new ScratchKey();
        scratch.Key.SetValue("Where", "the user's own setting");

        var signedIn = UserEnvironment.HivesFor(RunningAccount.Of(_environment));
        Assert.Same(UserHive.CurrentUser, signedIn.User);
        Assert.Null(signedIn.Running);

        var mine = WindowsIdentity.GetCurrent().User!.Value;
        _environment.WithSessionUser(new SignedInAccount(FakeUserEnvironment.SignedInName, mine));

        var elevated = UserEnvironment.HivesFor(RunningAccount.Of(_environment));
        Assert.NotSame(UserHive.CurrentUser, elevated.User);
        Assert.Equal("the user's own setting", elevated.User.ReadValue(scratch.Path, "Where"));
        Assert.Same(UserHive.CurrentUser, elevated.Running);

        var unconfirmed = UserEnvironment.HivesFor(RunningAccount.Of(_environment.WithNoSessionUser()));
        Assert.Null(unconfirmed.User.ReadValue(scratch.Path, "Where"));
        Assert.Same(UserHive.CurrentUser, unconfirmed.Running);
    }
}
