namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Create a symbolic link and prove this machine will follow it before a test relies on it.
///
/// <para>Creating a link proves nothing about following one. A process that enforces Redirection
/// Guard refuses every link an unelevated account made, with
/// <c>ERROR_UNTRUSTED_MOUNT_POINT</c>, and it passes that refusal to every child it starts. The
/// link itself still reads normally — <see cref="Directory.Exists(string)"/> answers true and the
/// attributes carry <see cref="FileAttributes.ReparsePoint"/> — while every path through it reads
/// as absent. A test built on it then fails with a bare assertion that points at production code
/// which is correct, and ruling that out has cost a whole session more than once.</para>
///
/// <para><see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> follows the link the same way a
/// provider's read through it does, so it raises the same refusal. Its message is the diagnosis, so
/// the failure quotes it rather than paraphrasing it.</para>
/// </summary>
public static class SymbolicLink
{
    public static void ToDirectory(string link, string target)
    {
        Directory.CreateSymbolicLink(link, target);
        ProveFollowed(new DirectoryInfo(link));
    }

    public static void ToFile(string link, string target)
    {
        File.CreateSymbolicLink(link, target);
        ProveFollowed(new FileInfo(link));
    }

    private static void ProveFollowed(FileSystemInfo link)
    {
        FileSystemInfo? final;

        try
        {
            final = link.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (IOException refused)
        {
            throw new InvalidOperationException(
                $"The symbolic link {link.FullName} was created, but this machine will not follow it: "
                + $"\"{refused.Message}\" Every path through it reads as absent, so a test built on "
                + "it fails for the environment rather than for the code under test. An untrusted "
                + "mount point means a process above the test host enforces Redirection Guard.",
                refused);
        }

        Assert.True(
            final is { Exists: true },
            $"The symbolic link {link.FullName} leads to {final?.FullName ?? "nothing"}, which does "
            + "not exist. Create the target before the link.");
    }
}
