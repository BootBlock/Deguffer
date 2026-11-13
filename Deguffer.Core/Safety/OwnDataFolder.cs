namespace Deguffer.Core.Safety;

/// <summary>
/// The one place that names the folder Deguffer keeps its own records in, <c>%LOCALAPPDATA%\Deguffer</c>.
///
/// <para>Named once so that a store, a test fake and anything that lists or removes Deguffer's records
/// cannot disagree about where they are. The stores written before this existed name the folder
/// themselves, each counted by the source sweep until it asks here instead.</para>
/// </summary>
public static class OwnDataFolder
{
    private const string FolderName = "Deguffer";

    /// <summary>The full path of Deguffer's own record <paramref name="fileName"/> on the machine <paramref name="environment"/> describes.</summary>
    public static string FileFor(IUserEnvironment environment, string fileName)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        return Path.Combine(environment.LocalAppData, FolderName, fileName);
    }
}
