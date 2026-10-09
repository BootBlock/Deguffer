using Deguffer.Core.Exploring.Acting;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// Which copies a duplicate search may never mark, and why, in a sentence the page shows (§7.4).
///
/// <para><b>Never marked, and still able to be kept.</b> A reference copy is the one the user said
/// they keep, and a file with several names frees nothing when one name goes. Either can be the
/// copy a group keeps.</para>
///
/// <para><b>Refused, and never kept either.</b> A copy is refused where Explore's policy would not
/// remove it (§7.1: Tier 4, a tool root's unrecognised child, a §9 exclusion, a protected region, an
/// Outlook data file), in a folder an installed program's entry names, and while it is online-only.
/// The policy is Explore's own, asked through <see cref="ExploreActionPolicy.MayRemove"/>, so the
/// two pages cannot come to disagree about what may go.</para>
///
/// <para>Each answer is kept for the life of the instance (G4): a page asks about every copy of
/// every group each time it draws, and the policy asks the machine where each path is mounted.</para>
/// </summary>
public sealed class CopyRefusals
{
    private readonly ExploreActionPolicy _policy;
    private readonly IReadOnlyList<ProgramFolder> _programs;
    private readonly Dictionary<string, string?> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="policy">Explore's policy for this machine, the one the search passed over places by.</param>
    /// <param name="programs">Every folder an installed program's entry names (<see cref="CandidateFinding.ProgramFolders"/>).</param>
    public CopyRefusals(ExploreActionPolicy policy, IReadOnlyList<ProgramFolder> programs)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(programs);

        _policy = policy;
        _programs = programs;
    }

    /// <summary>Why <paramref name="copy"/> is never marked though it may be kept, or null where nothing stops it.</summary>
    public static string? WhyNeverMarked(DuplicateCandidate copy)
    {
        ArgumentNullException.ThrowIfNull(copy);

        if (copy.Role == LocationRole.Reference)
        {
            return "This is in a location chosen as a reference, and a reference copy is never marked.";
        }

        return copy.HasSeveralNames
            ? $"This file has {copy.NameCount} names, and removing one of them would free nothing, so none is marked."
            : null;
    }

    /// <summary>Why <paramref name="copy"/> is refused, so never marked and never kept, or null where it is not.</summary>
    public string? WhyRefused(DuplicateCandidate copy)
    {
        ArgumentNullException.ThrowIfNull(copy);

        if (copy.Storage.HasFlag(FileStorage.CloudOnly))
        {
            return "This file is online-only, so it is not on this computer to compare, keep or remove.";
        }

        // Refused where every name is: a name that may go is one a removal could take, and one that
        // may be kept is enough for the file to be kept (§7.4).
        string? first = null;

        foreach (var name in copy.Names)
        {
            if (WhyRefused(name) is not { } why)
            {
                return null;
            }

            first ??= why;
        }

        return first;
    }

    /// <summary>Why the copy at <paramref name="path"/> is refused by where it is, or null where it is not.</summary>
    internal string? WhyRefused(string path)
    {
        if (_byPath.TryGetValue(path, out var known))
        {
            return known;
        }

        var verdict = _policy.MayRemove(path);
        var why = !verdict.IsAllowed
            ? verdict.Reason
            : _programs.FirstOrDefault(program => program.Reached.PathTo(path) is not null) is { } held
                ? $"'{held.Program}' is installed here, and a program's own files are never removed as duplicates: "
                  + "two programs that ship the same file each need their own copy."
                : null;

        return _byPath[path] = why;
    }
}
