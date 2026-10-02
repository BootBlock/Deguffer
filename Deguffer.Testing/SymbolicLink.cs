namespace Deguffer.Testing;

/// <summary>
/// Create a symbolic link and prove this machine will follow it before a test relies on it, for the
/// reason <see cref="FollowedLink"/> gives. A test of a folder link runs over
/// <see cref="DirectoryLink"/> instead, so it meets a junction as well.
/// </summary>
public static class SymbolicLink
{
    public static void ToDirectory(string link, string target)
    {
        Directory.CreateSymbolicLink(link, target);
        FollowedLink.Prove(new DirectoryInfo(link), "symbolic link");
    }

    public static void ToFile(string link, string target)
    {
        File.CreateSymbolicLink(link, target);
        FollowedLink.Prove(new FileInfo(link), "symbolic link");
    }
}
