using System.Security.AccessControl;
using System.Security.Principal;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A directory the current account may not read, in one of two ways, for the length of a
/// <c>using</c> block.
///
/// <para>Three rules in the suite are about what happens when the filesystem refuses — §5.3's
/// "treat access denied as normal and skip silently", the reach an enumerating route does not have,
/// and what a caller may conclude when it cannot read a path's attributes at all. None can be
/// tested against a fake, because what is under test is the refusal itself.</para>
///
/// <para>The deny goes on the DACL only and the account creating it stays the owner, so it can
/// always be taken off again — which is what makes this safe to leave in a suite that runs
/// unelevated. It is removed in <see cref="Dispose"/>, because a directory nothing can delete would
/// outlive the test run and the scratch root would leak.</para>
/// </summary>
public sealed class DeniedDirectory : IDisposable
{
    private readonly List<(DirectoryInfo Directory, FileSystemAccessRule Rule)> _applied = [];

    /// <summary>
    /// Refuse the right to <em>list</em> <paramref name="directory"/>, and nothing else.
    ///
    /// Traversing it is a separate right and is left alone, so a full path through it still
    /// resolves — which is exactly the situation an index-driven route meets, and the reason a
    /// candidate it offers from in there is a real one.
    /// </summary>
    public DeniedDirectory(string directory) => Build(() =>
    {
        Deny(directory, FileSystemRights.ListDirectory);

        Assert.Throws<UnauthorizedAccessException>(
            () => Directory.EnumerateFileSystemEntries(directory).ToList());
    });

    private DeniedDirectory(string directory, string parent) => Build(() =>
    {
        // Both ends, because either one alone leaves the attributes readable — measured rather than
        // assumed. NTFS answers GetFileAttributes out of the parent directory's own index whenever
        // the caller may list the parent, so denying the target its attribute right achieves
        // nothing on its own; and denying the parent achieves nothing while the target itself will
        // answer. Denying FullControl on the target alone leaves it readable for the same reason.
        Deny(parent, FileSystemRights.ListDirectory);
        Deny(directory, FileSystemRights.ReadAttributes);

        Assert.Throws<UnauthorizedAccessException>(() => File.GetAttributes(directory));
    });

    /// <summary>
    /// Refuse the attribute read itself, which takes an access rule on <paramref name="directory"/>
    /// and one on the directory above it.
    ///
    /// <para>The parent's DACL is changed as well as the target's, which is why this is a named
    /// factory rather than a constructor overload: a caller has to know that the directory above
    /// the one it named is altered too, and has to own it.</para>
    /// </summary>
    public static DeniedDirectory WithUnreadableAttributes(string directory) =>
        new(directory, Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar))
            ?? throw new ArgumentException("A volume root has no parent to deny.", nameof(directory)));

    public void Dispose()
    {
        // Reversed, so a parent whose own rule is still in place is not needed to reach a child.
        _applied.Reverse();

        foreach (var (directory, rule) in _applied)
        {
            Apply(directory, security => security.RemoveAccessRule(rule));
        }

        _applied.Clear();
    }

    private void Deny(string directory, FileSystemRights rights)
    {
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            rights,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny);

        var info = new DirectoryInfo(directory);
        Apply(info, security => security.AddAccessRule(rule));
        _applied.Add((info, rule));
    }

    /// <summary>
    /// Apply the rules and prove the refusal is real, undoing everything if any part of it throws.
    ///
    /// <para>The whole body is guarded, not just the assertion. <see cref="Deny"/> writes each rule
    /// to disk before recording it, and the two-ended mode writes two — so a throw on the second
    /// leaves the first standing with no object for a caller to dispose. That one is on the
    /// <em>parent</em>, and a Deny/ListDirectory there defeats
    /// <see cref="TempDirectory"/>'s own recursive delete, which swallows
    /// <see cref="UnauthorizedAccessException"/>. The scratch tree would then stay on the
    /// developer's disk for good, with nothing said and no test failing.</para>
    ///
    /// <para>The assertion itself is the other reason to guard: a machine that let the refusal
    /// through would make every assertion downstream pass for the wrong reason, and a TEMP that does
    /// not round-trip a DACL — a redirected share, a bind mount — is a real way for that to
    /// happen.</para>
    /// </summary>
    private void Build(Action apply)
    {
        try
        {
            apply();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static void Apply(DirectoryInfo info, Action<DirectorySecurity> change)
    {
        var security = info.GetAccessControl(AccessControlSections.Access);
        change(security);
        info.SetAccessControl(security);
    }
}
