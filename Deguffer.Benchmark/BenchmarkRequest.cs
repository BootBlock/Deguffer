using System.Globalization;

namespace Deguffer.Benchmark;

/// <summary>
/// What to time, read from the command line.
/// </summary>
/// <param name="Path">
/// For a route that reads the table, the root of the volume, such as <c>C:\</c>. For the walk, the
/// folder, fully qualified. Never printed: see <see cref="MeasuredPlace"/>.
/// </param>
/// <param name="Runs">How many times to run the route. The first is reported apart from the rest.</param>
internal sealed record BenchmarkRequest(Route Route, string Path, int Runs)
{
    public const int DefaultRuns = 5;

    public const int MaximumRuns = 1000;

    public const string Usage =
        """
        Usage: Deguffer.Benchmark <route> <target> [--runs N]

        Routes:
          table <drive>     Read the volume's file table end to end, parse only. Elevated.
          index <drive>     Build the file-table index the deletion path measures from. Elevated.
          explore <drive>   Build the Explore tree for the volume from its file table. Elevated.
          walk <folder>     Walk the folder as an unelevated scan does.

        --runs N   How many times to run the route, 1 to 1000. The default is 5. The first run is
                   reported apart from the others.

        It only reads. Nothing on the drive is written, moved or deleted.
        """;

    /// <summary>The volume's drive letter. Meaningful only for a route that reads the table.</summary>
    public char Drive => Path[0];

    /// <summary>
    /// Read <paramref name="args"/>, or return null with what was wrong in
    /// <paramref name="error"/>.
    /// </summary>
    public static BenchmarkRequest? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;

        if (args.Count < 2)
        {
            error = "Name a route and a target.";
            return null;
        }

        // By name alone. Enum.TryParse would also take "1" and "table, index", neither of which names
        // a route.
        var named = Enum.GetValues<Route>()
            .Where(r => string.Equals(r.ToString(), args[0], StringComparison.OrdinalIgnoreCase))
            .Cast<Route?>()
            .SingleOrDefault();

        if (named is not { } route)
        {
            error = "The route must be table, index, explore or walk.";
            return null;
        }

        var runs = DefaultRuns;

        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] == "--runs"
                && i + 1 < args.Count
                && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out runs)
                && runs is >= 1 and <= MaximumRuns)
            {
                i++;
                continue;
            }

            error = $"--runs takes a number from 1 to {MaximumRuns}, and nothing else is accepted after the target.";
            return null;
        }

        var path = route.ReadsTable() ? VolumeRoot(args[1]) : Folder(args[1]);
        if (path is null)
        {
            error = route.ReadsTable()
                ? "The target must be a drive letter, such as C or C:."
                : "The target must be a folder.";
            return null;
        }

        return new BenchmarkRequest(route, path, runs);
    }

    /// <summary>
    /// <c>C</c>, <c>C:</c> or <c>C:\</c> as the volume's root, or null. The table belongs to a
    /// volume rather than to a folder, so a folder here would be a request this route cannot honour.
    /// </summary>
    private static string? VolumeRoot(string target)
    {
        var letter = target.TrimEnd('\\');
        if (letter.Length == 2 && letter[1] == ':')
        {
            letter = letter[..1];
        }

        return letter.Length == 1 && char.IsAsciiLetter(letter[0])
            ? $"{char.ToUpperInvariant(letter[0])}:\\"
            : null;
    }

    /// <summary>
    /// The folder fully qualified, or null where Windows will not accept the text as a path. A
    /// relative folder is resolved against the working directory, which is the one the person
    /// running this typed it in.
    /// </summary>
    private static string? Folder(string target)
    {
        try
        {
            return System.IO.Path.GetFullPath(target);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
