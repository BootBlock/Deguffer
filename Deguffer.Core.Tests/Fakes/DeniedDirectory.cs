using System.Security.AccessControl;
using System.Security.Principal;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A directory the current account may not list, for the length of a <c>using</c> block.
///
/// <para>Two rules in the suite are about what happens when the filesystem refuses — §5.3's "treat
/// access denied as normal and skip silently", and the reach an enumerating route does not have —
/// and neither can be tested against a fake, because what is under test is the refusal itself.</para>
///
/// <para>The deny goes on the DACL only and the account creating it stays the owner, so it can
/// always be taken off again — which is what makes this safe to leave in a suite that runs
/// unelevated. It is removed in <see cref="Dispose"/>, because a directory nothing can delete would
/// outlive the test run and the scratch root would leak.</para>
///
/// <para>Only the right to <em>list</em> the directory is removed. Traversing it is a separate right
/// and is left alone, so a full path through it still resolves — which is exactly the situation an
/// index-driven route meets, and the reason a candidate it offers from in there is a real one.</para>
/// </summary>
public sealed class DeniedDirectory : IDisposable
{
    private readonly string _directory;
    private readonly FileSystemAccessRule _rule;

    public DeniedDirectory(string directory)
    {
        _directory = directory;
        _rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);

        Apply(security => security.AddAccessRule(_rule));

        try
        {
            // The fixture is only a fixture if the refusal is real. A machine that let this through
            // would make every assertion downstream pass for the wrong reason.
            Assert.Throws<UnauthorizedAccessException>(
                () => Directory.EnumerateFileSystemEntries(directory).ToList());
        }
        catch
        {
            // The rule is on disk from the line above, and a throw here means no caller ever got an
            // object to dispose. A TEMP that does not round-trip a DACL — a redirected share, a bind
            // mount — would otherwise leave a directory the suite cannot delete and the user cannot
            // find, once per run.
            Dispose();
            throw;
        }
    }

    public void Dispose() => Apply(security => security.RemoveAccessRule(_rule));

    private void Apply(Action<DirectorySecurity> change)
    {
        var info = new DirectoryInfo(_directory);
        var security = info.GetAccessControl(AccessControlSections.Access);
        change(security);
        info.SetAccessControl(security);
    }
}
