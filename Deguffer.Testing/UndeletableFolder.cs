using System.Security.AccessControl;
using System.Security.Principal;

namespace Deguffer.Testing;

/// <summary>
/// An empty folder the current account may not delete, for the length of a <c>using</c> block:
/// Windows' own refusal, for the tests that need it to reach the deletion of a folder exactly as an
/// access rule on a real machine would. The folder counterpart of <see cref="UndeletableFile"/>, and
/// built the same way for the same reason: the folder is denied <c>DELETE</c> and the folder holding
/// it is denied <c>FILE_DELETE_CHILD</c>, because either alone lets the deletion through.
/// </summary>
public sealed class UndeletableFolder : IDisposable
{
    private readonly DirectoryInfo _folder;
    private readonly DirectoryInfo _parent;
    private readonly FileSystemAccessRule _folderRule;
    private readonly FileSystemAccessRule _parentRule;

    public UndeletableFolder(string path)
    {
        _folder = new DirectoryInfo(path);
        _parent = _folder.Parent!;

        var account = WindowsIdentity.GetCurrent().User!;
        _folderRule = new FileSystemAccessRule(account, FileSystemRights.Delete, AccessControlType.Deny);
        _parentRule = new FileSystemAccessRule(
            account, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);

        try
        {
            Apply(_folder, security => security.AddAccessRule(_folderRule));
            Apply(_parent, security => security.AddAccessRule(_parentRule));

            // A volume that does not round-trip a DACL would let the deletion through, and every
            // assertion downstream would then pass for the wrong reason.
            Assert.Throws<IOException>(() => Directory.Delete(path));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Lifts both rules. Removing a rule that was never added is a no-op, so this is also what undoes
    /// a constructor that failed part-way.
    /// </summary>
    public void Dispose()
    {
        Apply(_parent, security => security.RemoveAccessRule(_parentRule));
        Apply(_folder, security => security.RemoveAccessRule(_folderRule));
    }

    private static void Apply(DirectoryInfo folder, Action<DirectorySecurity> change)
    {
        var security = folder.GetAccessControl(AccessControlSections.Access);
        change(security);
        folder.SetAccessControl(security);
    }
}
