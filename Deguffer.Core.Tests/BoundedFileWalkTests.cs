using System.Security.AccessControl;
using System.Security.Principal;
using Deguffer.Core.Scanning;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// §5.3's other half, which had no test in either scanner until the walk became one seam.
///
/// "Treat 'access denied' as normal and skip silently — a locked file is the OS protecting live
/// state" is a safety rule, and an untested one is a rule that can be deleted without anything
/// noticing: removing the catch filter outright left the whole suite green. It is tested here
/// rather than in a scanner's own class because both scanners now reach it through
/// <see cref="BoundedFileWalk"/>, so one test covers both.
/// </summary>
public sealed class BoundedFileWalkTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A directory the current account is denied even the right to list.
    ///
    /// The deny is on the DACL only, and the account creating it stays the owner, so it can be
    /// taken off again — which is what makes this safe to leave in a suite. The rule is restored in
    /// a <c>finally</c>, because a directory nothing can delete would outlive the test run.
    /// </summary>
    private static FileSystemAccessRule DenyListing(string directory)
    {
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);

        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(rule);
        info.SetAccessControl(security);

        return rule;
    }

    private static void Restore(string directory, FileSystemAccessRule rule)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl(AccessControlSections.Access);
        security.RemoveAccessRule(rule);
        info.SetAccessControl(security);
    }

    /// <summary>
    /// The scan reports what it could read and does not fail, which is §5.3 exactly. Both halves
    /// matter: an exception here would take a whole preview down over one protected folder, and
    /// counting the unreadable subtree would promise bytes no deletion could reclaim.
    /// </summary>
    [Fact]
    public async Task ARefusedDirectoryIsSkippedAndTheRestOfTheTreeStillCounts()
    {
        var root = _temp.CreateDirectory("cache");
        _temp.CreateFile(4096, "cache", "readable.bin");
        var refused = _temp.CreateDirectory("cache", "refused");
        _temp.CreateFile(65536, "cache", "refused", "unreachable.bin");

        var rule = DenyListing(refused);

        try
        {
            // The fixture is only a fixture if the refusal is real: a machine that let this
            // through would make the assertion below pass for the wrong reason.
            Assert.Throws<UnauthorizedAccessException>(
                () => Directory.EnumerateFileSystemEntries(refused).ToList());

            var measured = await ParallelEnumerationScanner.Default.MeasureAsync(root);

            Assert.Equal(4096, measured.Size.Logical);
        }
        finally
        {
            Restore(refused, rule);
        }
    }
}
