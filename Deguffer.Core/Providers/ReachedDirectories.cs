using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Several directories a provider reaches by name, sorted by what Windows said about each: the ones
/// that are there, and the ones it would not describe. The ones that are not there are dropped,
/// because nothing is owed about them.
///
/// <para><b>For a provider whose tool names its caches rather than one root.</b>
/// <c>dotnet nuget locals</c>, <c>conda info</c>, <c>go env</c> and <c>poetry cache list</c> each
/// answer with a list, and filtering that list through <see cref="LongPath.DirectoryExists"/> reads
/// a location Windows would not describe as one that is not there. The plan then says the tool
/// "has cached nothing yet" about a cache on the disk, which is the sentence
/// <see cref="CleanupProviderBase.NothingToPlanFor(string, string)"/> ended for a single root. See
/// <see cref="PathPresence"/>.</para>
/// </summary>
/// <param name="Present">The directories that are there, in the order they were named.</param>
/// <param name="Refused">The directories Windows would not describe, in the order they were named.</param>
internal sealed record ReachedDirectories(IReadOnlyList<string> Present, IReadOnlyList<string> Refused)
{
    public static ReachedDirectories Of(IEnumerable<string> paths)
    {
        var present = new List<string>();
        var refused = new List<string>();

        foreach (var path in paths)
        {
            switch (LongPath.ProbeDirectory(path))
            {
                case PathPresence.Present:
                    present.Add(path);
                    break;

                case PathPresence.Refused:
                    refused.Add(path);
                    break;
            }
        }

        return new ReachedDirectories(present, refused);
    }

    /// <summary>Whether the plan owes <see cref="CleanupPlan.HasUnreadableRoot"/>.</summary>
    public bool CouldNotBeReached => Refused.Count > 0;

    /// <summary>One warning per directory Windows would not describe.</summary>
    public IEnumerable<PlanNote> UnreachedNotes => Refused.Select(UnreadableRoot.UnreachedNote);
}
