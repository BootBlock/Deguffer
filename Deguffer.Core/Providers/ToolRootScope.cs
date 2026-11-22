using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// The folders every root in a row's <see cref="ICleanupProvider.ToolRoots"/> lies at or under,
/// stated without touching the disk, so a question about one folder reads only the rows that could
/// declare it.
///
/// <para><b>Why it exists.</b> <see cref="RowDeclarations"/> answers whether another row offers a
/// child of a folder, and a row may build its declaration from a walk: the Claude Code conversations
/// row reads thousands of entries and the Chromium rows walk both application-data roots. Asked of
/// every row, the Gradle row's question about its own home waited for both, though neither can ever
/// declare a root there.</para>
///
/// <para><b>A scope that misses a root fails towards a false alarm.</b> The missed root's offers are
/// not seen by a row asking about its folder, so that row asserts as a survivor a child the first row
/// removes, and §5.6 reports a correct removal as a failure. It never adds a deletion. A test holds
/// every row's roots inside its scope.</para>
/// </summary>
public sealed class ToolRootScope
{
    /// <summary>Declares no root: the scope of a row whose <see cref="ICleanupProvider.ToolRoots"/> is empty.</summary>
    public static readonly ToolRootScope None = new([]);

    /// <summary>
    /// May declare a root anywhere, so the row is read for every question: for a row that learns
    /// where its roots are only from the disk, such as a folder a settings file names.
    /// </summary>
    public static readonly ToolRootScope Anywhere = new(null);

    // Null for Anywhere. Each in the form LongPath.Configured gives, which is how RowDeclarations
    // keys a root and a question.
    private readonly string[]? _folders;

    private ToolRootScope(string[]? folders) => _folders = folders;

    /// <summary>
    /// The scope of the roots at or under <paramref name="folders"/>. A folder that is null or not
    /// fully qualified is left out, since a root at that path is not one
    /// <see cref="RowDeclarations"/> can key either.
    /// </summary>
    public static ToolRootScope Under(params IEnumerable<string?> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        string[] configured = [.. folders.Select(LongPath.Configured).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
        return configured.Length == 0 ? None : new ToolRootScope(configured);
    }

    /// <summary>Whether this scope may hold a root at <paramref name="folder"/>, given in the form <see cref="LongPath.Configured"/> gives.</summary>
    internal bool Covers(string folder)
    {
        if (_folders is null)
        {
            return true;
        }

        // A loop rather than a query: asked once for each row each time a plan asks about a folder.
        foreach (var scope in _folders)
        {
            if (LongPath.Contains(scope, folder))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether no root can be declared: such a row is never read for a question.</summary>
    internal bool IsEmpty => _folders is { Length: 0 };

    public override string ToString() =>
        _folders is null ? "anywhere" : _folders.Length == 0 ? "none" : string.Join("; ", _folders);
}
