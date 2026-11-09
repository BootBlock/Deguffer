using Deguffer.Core.Safety;

namespace Deguffer.Core.Duplicates;

/// <summary>
/// The folders programs are installed in, asked which of them holds a path, as a duplicate search
/// asks of every copy (#706).
/// </summary>
internal sealed class ProgramFolderIndex
{
    private readonly FolderIndex<(int Position, ProgramFolder Program)> _byPlace;

    public ProgramFolderIndex(IReadOnlyList<ProgramFolder> programs) =>
        _byPlace = new FolderIndex<(int Position, ProgramFolder Program)>(
            programs.Select((program, position) => (position, program)),
            entry => entry.Program.Forms.SelectMany(form => form.Places));

    /// <summary>
    /// The first of the programs, in the order they were given, whose folder holds
    /// <paramref name="path"/>, or null where none does. The walk comes to the deepest folder first
    /// and the first program may be above it, so every folder is asked.
    /// </summary>
    public ProgramFolder? FirstHolding(string path)
    {
        ProgramFolder? first = null;
        var earliest = int.MaxValue;

        foreach (var entries in _byPlace.Holding(path))
        {
            foreach (var (position, program) in entries)
            {
                if (position < earliest && program.Holds(path))
                {
                    first = program;
                    earliest = position;
                }
            }
        }

        return first;
    }
}
