using Deguffer.Core.Safety;
using Deguffer.Core.Startup;

namespace Deguffer.Testing;

/// <summary>
/// Programs as a test describes them: whether each is there and what it says about itself. A program
/// nobody described is one Windows says is not there.
/// </summary>
public sealed class FakeProgramFiles : IProgramFiles
{
    private readonly Dictionary<string, (PathPresence Presence, ProgramDescription Description)> _programs =
        new(StringComparer.OrdinalIgnoreCase);

    public FakeProgramFiles With(string path, string? description = null, string? company = null, PathPresence presence = PathPresence.Present)
    {
        _programs[path] = (presence, new ProgramDescription(description, company));
        return this;
    }

    public PathPresence Probe(string path) => _programs.TryGetValue(path, out var program) ? program.Presence : PathPresence.Absent;

    public ProgramDescription Describe(string path) =>
        _programs.TryGetValue(path, out var program) ? program.Description : ProgramDescription.None;
}
