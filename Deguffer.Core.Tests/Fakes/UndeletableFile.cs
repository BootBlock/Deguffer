using System.Security.AccessControl;
using System.Security.Principal;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A file the current account may not delete, for the length of a <c>using</c> block — Windows'
/// own refusal rather than a fake's, for the tests that need it to reach <c>DeleteFile</c> and the
/// open for deletion exactly as a guarded file on a real machine would.
///
/// <para><b>Both ends, because either alone lets the deletion through.</b> Windows grants a delete
/// when the file allows it <em>or</em> when the directory holding it allows its children to be
/// deleted, so the file is denied <c>DELETE</c> and its directory is denied
/// <c>FILE_DELETE_CHILD</c>. Every sibling still allows its own deletion, so nothing else in the
/// directory is affected.</para>
///
/// <para>The rules go on the DACL only and the account stays the owner, so they can always be taken
/// off again — see <see cref="DeniedDirectory"/>, whose reasoning this follows. They are removed in
/// <see cref="Dispose"/>, or the scratch tree would outlive the run.</para>
/// </summary>
public sealed class UndeletableFile : IDisposable
{
    private readonly FileInfo _file;
    private readonly DirectoryInfo _directory;
    private readonly FileSystemAccessRule _fileRule;
    private readonly FileSystemAccessRule _directoryRule;

    public UndeletableFile(string path)
    {
        _file = new FileInfo(path);
        _directory = _file.Directory!;

        var account = WindowsIdentity.GetCurrent().User!;
        _fileRule = new FileSystemAccessRule(account, FileSystemRights.Delete, AccessControlType.Deny);
        _directoryRule = new FileSystemAccessRule(
            account, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);

        try
        {
            ApplyToFile(security => security.AddAccessRule(_fileRule));
            ApplyToDirectory(security => security.AddAccessRule(_directoryRule));

            // A volume that does not round-trip a DACL would let the deletion through, and every
            // assertion downstream would then pass for the wrong reason.
            Assert.Throws<UnauthorizedAccessException>(() => File.Delete(path));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Lifts both rules. Removing a rule that was never added is a no-op, so this is also what
    /// undoes a constructor that failed part-way.
    /// </summary>
    public void Dispose()
    {
        ApplyToDirectory(security => security.RemoveAccessRule(_directoryRule));
        ApplyToFile(security => security.RemoveAccessRule(_fileRule));
    }

    private void ApplyToFile(Action<FileSecurity> change)
    {
        var security = _file.GetAccessControl(AccessControlSections.Access);
        change(security);
        _file.SetAccessControl(security);
    }

    private void ApplyToDirectory(Action<DirectorySecurity> change)
    {
        var security = _directory.GetAccessControl(AccessControlSections.Access);
        change(security);
        _directory.SetAccessControl(security);
    }
}
