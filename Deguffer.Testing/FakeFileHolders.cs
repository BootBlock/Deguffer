using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// The Restart Manager a test says Windows would give: which files a process holds open, and which
/// paths it refuses to register. Every session asked is recorded with the files it was given.
/// </summary>
internal sealed class FakeFileHolders
{
    private readonly Dictionary<string, string> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IReadOnlyList<string>> _sessions = [];

    /// <summary>Each session asked, in order, with the files registered in it.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Sessions => _sessions;

    public FakeFileHolders Holding(string file, string holder)
    {
        _held[file] = holder;
        return this;
    }

    /// <summary>
    /// A file whose registration fails, as one past <c>MAX_PATH</c> does, which leaves every session
    /// it is registered in unanswered.
    /// </summary>
    public FakeFileHolders Refusing(string file)
    {
        _refused.Add(file);
        return this;
    }

    /// <summary>Answers as one session does: every holder of any file in it, with no word on which file.</summary>
    public FileHolders Query(IReadOnlyList<string> files, CancellationToken ct)
    {
        _sessions.Add([.. files]);

        if (files.Any(_refused.Contains))
        {
            return FileHolders.Unanswered;
        }

        return new FileHolders(
            [.. files.Where(_held.ContainsKey).Select(file => _held[file]).Distinct(StringComparer.Ordinal)],
            Answered: true);
    }
}
