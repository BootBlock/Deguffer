using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Conan's search for a <c>.conanrc</c>: <c>find_file_walk_up</c> in <c>conan/internal/paths.py</c>,
/// Conan 2.34.0 (commit <c>70b4cf60b5787ebf1a1af16ac45a11491be0d4c3</c>). The folder itself, then each
/// folder above it, up to and including <paramref name="End"/>, or the root of its drive where none is
/// given. Conan calls it with none, which is <see cref="ToDriveRoot"/>.
///
/// <para><b>Why an end at all.</b> It is Conan's own parameter, and the one bound that keeps a test's
/// walk inside the folder the test made: walking on reads whatever <c>.conanrc</c> the machine
/// running the test has above its temporary folder.</para>
/// </summary>
/// <param name="End">The last folder searched, or null for the root of the drive.</param>
internal sealed record ConanRcWalk(string? End)
{
    /// <summary>The walk Conan makes.</summary>
    public static readonly ConanRcWalk ToDriveRoot = new((string?)null);

    /// <summary>
    /// The first <c>.conanrc</c> at or above <paramref name="workingDirectory"/>, or null where there is
    /// none. A refusal stops the walk, because Python's answer to one depends on its version: an older
    /// one gives up on the whole file and a newer one walks on past it.
    /// </summary>
    public ConanRcSearch? Find(string workingDirectory)
    {
        for (var folder = workingDirectory; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var candidate = Path.Combine(folder, ConanRc.FileName);

            switch (LongPath.ProbeFile(candidate))
            {
                case PathPresence.Present:
                    return new ConanRcSearch.Found(candidate);

                case PathPresence.Refused:
                    return new ConanRcSearch.Refused(candidate);
            }

            if (End is { } end && Path.TrimEndingDirectorySeparator(folder).Equals(Path.TrimEndingDirectorySeparator(end), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return null;
    }
}

/// <summary>What <see cref="ConanRcWalk.Find"/> came to, where it found anything.</summary>
internal abstract record ConanRcSearch
{
    public sealed record Found(string Path) : ConanRcSearch;

    /// <summary>Windows would not say whether a <c>.conanrc</c> is at <paramref name="Path"/>.</summary>
    public sealed record Refused(string Path) : ConanRcSearch;
}
