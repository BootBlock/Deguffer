using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Whether <c>pip --cache-dir &lt;folder&gt; cache purge</c>, run now, would still reach only the folders
/// of pip's layout the plan examined.
///
/// <para><b>Why the run's own look is not enough.</b> The purge walks <c>http</c>, <c>http-v2</c> and
/// <c>wheels</c> under the folder it is given with Python's <c>os.walk</c>, which enters a junction. The
/// look before the command names a link inside those folders, but each folder is listed as it stands,
/// so one replaced by a junction after the preview is listed through the junction and passes. And a
/// folder absent from the plan is not looked at at all, though pip walks it if it has appeared since.
/// So each of the three, and the folder above them, is asked again here whether it is a link.</para>
/// </summary>
/// <param name="cache">The folder pip is sent to.</param>
internal sealed class PipLayoutTarget(string cache) : ICommandTargetCheck
{
    /// <summary>The folders pip's purge empties under the cache directory it is given.</summary>
    public static readonly IReadOnlySet<string> Folders =
        new HashSet<string>(["http", "http-v2", "wheels"], StringComparer.OrdinalIgnoreCase);

    public string? WhyNot(RunCommandStep step, CancellationToken ct)
    {
        foreach (var path in Folders.Select(folder => Path.Combine(cache, folder)).Prepend(cache))
        {
            if (LongPath.IsReparsePoint(path))
            {
                return $"{LongPath.Display(path)} is now a link to somewhere else, and pip's purge would follow it and "
                    + "remove what is on the far side";
            }
        }

        return null;
    }
}
