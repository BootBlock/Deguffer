namespace Deguffer.Core.Providers;

/// <summary>
/// One inference runtime LM Studio reports as installed.
/// </summary>
/// <param name="Name">
/// The runtime line, such as <c>llama.cpp-win-x86_64-nvidia-cuda12-avx2</c>: one engine built for one
/// hardware target. LM Studio keeps every version of a line it has downloaded.
/// </param>
/// <param name="Version">The version, verbatim, as LM Studio printed it.</param>
/// <param name="IsSelected">
/// Whether LM Studio marks this runtime as the one it uses. It keeps one selection per model format,
/// so more than one runtime can be selected at once.
/// </param>
internal sealed record LmStudioRuntime(string Name, string Version, bool IsSelected)
{
    /// <summary>What <c>lms</c> names the runtime by, and the only form its remove command matches one version by.</summary>
    public string Id => $"{Name}@{Version}";

    /// <summary>The folder LM Studio keeps the runtime in, inside its runtimes folder.</summary>
    public string Folder => $"{Name}-{Version}";
}

/// <summary>
/// Reads what <c>lms runtime ls</c> printed.
///
/// <para><b>Scraping text, and not by preference.</b> The command has no machine-readable form:
/// <c>--json</c> is refused as an unknown option, and only <c>lms runtime survey</c> offers one. It
/// prints a table of three columns, <c>LLM ENGINE</c>, <c>SELECTED</c> and <c>MODEL FORMAT</c>, laid
/// out by the widest value in each. So the parse leans on as little of the layout as it can: the
/// runtime is the first field of a row, and a row is selected where anything at all stands under the
/// <c>SELECTED</c> heading.</para>
///
/// <para><b>Why the mark is found by position rather than by what it is.</b> LM Studio writes a
/// check mark, and a pipe read under the console's code page rather than UTF-8 turns it into several
/// other characters. Everything before the mark is the runtime and padding, so the mark still starts
/// inside the column whatever it has been turned into.</para>
///
/// <para><b>An unreadable listing answers null, and the caller then offers nothing.</b> The selection
/// is what keeps the runtime in use from being removed, and a guess about it is a guess about which
/// runtime LM Studio needs to run at all.</para>
/// </summary>
internal static class LmStudioRuntimeList
{
    private const string EngineHeading = "LLM ENGINE";

    private const string SelectedHeading = "SELECTED";

    private const string FormatHeading = "MODEL FORMAT";

    /// <summary>What the command prints in place of a table when nothing is installed.</summary>
    private const string NothingInstalled = "No runtimes found.";

    /// <summary>The runtimes listed, which is empty where none are installed, or null where the listing could not be read.</summary>
    public static IReadOnlyList<LmStudioRuntime>? TryRead(string standardOutput)
    {
        var lines = standardOutput
            .Split('\n')

            // Only the carriage return the pipe preserves: the leading padding is where the columns are.
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines is [var only] && only.Trim() == NothingInstalled)
        {
            return [];
        }

        var heading = lines.FindIndex(line =>
            line.StartsWith(EngineHeading, StringComparison.Ordinal)
            && line.Contains(SelectedHeading, StringComparison.Ordinal));

        if (heading < 0)
        {
            return null;
        }

        var selectedFrom = lines[heading].IndexOf(SelectedHeading, StringComparison.Ordinal);
        var formatFrom = lines[heading].IndexOf(FormatHeading, selectedFrom, StringComparison.Ordinal);
        var runtimes = new List<LmStudioRuntime>(lines.Count - heading - 1);

        foreach (var line in lines.Skip(heading + 1))
        {
            if (TryReadRow(line, selectedFrom, formatFrom) is not { } runtime)
            {
                return null;
            }

            runtimes.Add(runtime);
        }

        // A runtime listed twice could be selected on one row and not on the other, and the second row
        // would then be offered. LM Studio lists each once, so a second row is output this cannot read.
        return runtimes.DistinctBy(runtime => runtime.Id, StringComparer.OrdinalIgnoreCase).Count() == runtimes.Count
            ? runtimes
            : null;
    }

    /// <summary>
    /// One row, or null where it is not one. A row that cannot be read fails the whole listing rather
    /// than being skipped, because the row skipped could be the selected runtime, and every other
    /// version of its line would then be offered.
    /// </summary>
    private static LmStudioRuntime? TryReadRow(string line, int selectedFrom, int formatFrom)
    {
        if (char.IsWhiteSpace(line[0]))
        {
            return null;
        }

        var end = line.IndexOfAny([' ', '\t']);
        var id = end < 0 ? line : line[..end];

        // The last '@', because the version is what follows it and a name has never held one.
        var at = id.LastIndexOf('@');

        if (at <= 0 || at == id.Length - 1 || end < 0 || end > selectedFrom)
        {
            return null;
        }

        var markEnd = formatFrom < 0 ? line.Length : Math.Min(formatFrom, line.Length);
        var selected = line.Length > selectedFrom
            && !string.IsNullOrWhiteSpace(line[selectedFrom..Math.Max(selectedFrom, markEnd)]);

        return new LmStudioRuntime(id[..at], id[(at + 1)..], selected);
    }
}
