using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What one repository in the Hugging Face hub cache takes up, as <c>hf cache ls</c> says now: its
/// figure while it is listed, and nothing once it is not.
///
/// <para><b>Why the disk cannot be measured instead.</b> With the shared blob store a repository's
/// folder holds links, and its files are in <c>hub\blobs</c> beside every other repository's, so a
/// measurement of the folder finds almost nothing either side of the command. The tool's figure is
/// the one the plan offered the repository at, so the run subtracts like from like.</para>
/// </summary>
/// <param name="Runner">What runs <paramref name="Hf"/>.</param>
/// <param name="Hf">The <c>hf</c> the plan asked.</param>
/// <param name="HubCache">The hub cache the plan examined, which the listing is told with <c>--cache-dir</c>.</param>
/// <param name="Id">The repository, as the command names it.</param>
internal sealed record HuggingFaceRepositorySize(IProcessRunner Runner, string Hf, string HubCache, string Id) : IToolMeasurement
{
    public async Task<long?> MeasureAsync(CancellationToken ct)
    {
        var listed = await Runner.RunAsync(Hf, HuggingFaceCommands.List(HubCache), ToolRun.Query, ct).ConfigureAwait(false);

        if (!listed.Succeeded || HuggingFaceCacheListing.TryRead(listed.StandardOutput) is not { } repositories)
        {
            return null;
        }

        return repositories.FirstOrDefault(repository => repository.Id.Equals(Id, StringComparison.OrdinalIgnoreCase)) is { } still
            ? still.Bytes
            : 0;
    }
}
