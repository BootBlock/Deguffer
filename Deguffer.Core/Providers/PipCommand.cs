using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Where pip is installed, for every row whose step is one of pip's own commands.
/// </summary>
internal static class PipCommand
{
    /// <summary>
    /// The commands pip may be installed as. Order matters only in that the first one found wins;
    /// both spellings drive the same pip, and either reports and empties whichever cache directory it
    /// is asked about.
    /// </summary>
    private static readonly string[] Names = ["pip", "pip3"];

    /// <summary>The pip on the PATH, or null where neither spelling is installed.</summary>
    public static string? Find(IUserEnvironment environment) =>
        Names.Select(environment.FindExecutable).FirstOrDefault(found => found is not null);
}
