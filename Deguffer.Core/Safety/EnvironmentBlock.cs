using System.Collections.Concurrent;
using System.Text;

namespace Deguffer.Core.Safety;

/// <summary>
/// The environment as this process should now see it: the machine and user variables Windows
/// composes at logon, read again, with whatever the launching process set differently left alone.
///
/// <para><b>Exists because Windows never pushes an environment change into a process that is
/// already running.</b> An installer that adds its directory to <c>PATH</c>, or that relocates a
/// cache through a variable, broadcasts <c>WM_SETTINGCHANGE</c> and writes the registry; a running
/// program keeps the block it was given. Without this, a toolchain installed while Deguffer is open
/// stays invisible to Storage and to Explore until the app is restarted from a refreshed
/// environment — and Explore goes on allowing the folders that tool's provider would protect
/// (§7.1).</para>
///
/// <para>Composition only. Reading the registry belongs to <see cref="UserEnvironment"/>, which
/// hands the two sets of values here, so what the rules below decide can be asserted without a
/// machine to change (G2).</para>
/// </summary>
internal sealed class EnvironmentBlock
{
    /// <summary>
    /// What <c>PATHEXT</c> falls back to when nothing names it, matching <c>cmd</c>'s own default.
    /// </summary>
    private const string DefaultPathExtensions = ".COM;.EXE;.BAT;.CMD";

    /// <summary>
    /// How many times a value is expanded before the result is taken as final.
    ///
    /// <para>Windows expands a <c>REG_EXPAND_SZ</c> value exactly once, so one pass resolves every
    /// ordinary entry. A second and third resolve a variable written in terms of another one —
    /// <c>%CUDA_PATH%\bin</c> where <c>CUDA_PATH</c> is itself <c>%ProgramFiles%\NVIDIA</c> — and
    /// the bound is what stops <c>PATH=%PATH%;…</c> from expanding forever.</para>
    /// </summary>
    private const int ExpansionPasses = 4;

    /// <summary>
    /// The variable Windows appends rather than replaces, which is why a user <c>PATH</c> extends
    /// the system one instead of hiding it.
    ///
    /// <para><c>PATHEXT</c> is deliberately <em>not</em> here, though it is a list too: Windows
    /// appends only <c>Path</c> (and the OS/2 library variables nothing has used in decades), so a
    /// user <c>PATHEXT</c> replaces the machine's. Appending it as well would have Deguffer search
    /// extensions the shell would not run, in an order it would not use.</para>
    /// </summary>
    private static readonly HashSet<string> Appended = new(StringComparer.OrdinalIgnoreCase)
    {
        "Path",
    };

    /// <summary>
    /// The variables that name a search rather than a place, and so are governed by the list rules
    /// rather than the single-value ones: composed by adding to what the process has, never
    /// protected as a process override, and written back from the search they describe.
    /// </summary>
    private static readonly HashSet<string> Searched = new(StringComparer.OrdinalIgnoreCase)
    {
        "Path",
        "PATHEXT",
    };

    private static readonly IReadOnlySet<string> NoOverrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyDictionary<string, string> _variables;

    /// <summary>
    /// The names the launching process set to something the registry did not account for. Fixed at
    /// start-up, and carried through every refresh: a variable a developer's shell exported before
    /// starting Deguffer is a deliberate choice about this session, and a refresh that overwrote it
    /// would send a provider to a cache the user is not using.
    /// </summary>
    private readonly IReadOnlySet<string> _overridden;

    private EnvironmentBlock(
        IReadOnlyDictionary<string, string> variables,
        IReadOnlySet<string> overridden,
        IReadOnlyList<string> pathDirectories,
        IReadOnlyList<string> pathExtensions)
    {
        _variables = variables;
        _overridden = overridden;
        PathDirectories = pathDirectories;
        PathExtensions = pathExtensions;
    }

    /// <summary>The directories a command is searched for in, in the order they are searched.</summary>
    public IReadOnlyList<string> PathDirectories { get; }

    /// <summary>The extensions appended to a command that was named without one.</summary>
    public IReadOnlyList<string> PathExtensions { get; }

    /// <summary>
    /// Where a command was found, for the life of this block.
    ///
    /// <para>Resolving one probes the filesystem across every directory above, and both
    /// <c>IsPresentAsync</c> and <c>PlanAsync</c> ask about the same tools, so the answer is worth
    /// keeping (G4). It belongs to the block rather than to <see cref="UserEnvironment"/> so that
    /// dropping a stale environment drops what it resolved in the same reference swap: a cache
    /// cleared beside the block it was derived from can be refilled from the old one by a thread
    /// that is midway through a lookup.</para>
    /// </summary>
    public ConcurrentDictionary<string, string?> Resolved { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The block the process was given, and which of its variables the registry did not account
    /// for.
    /// </summary>
    /// <param name="process">This process's environment, as Windows built it at start-up.</param>
    /// <param name="machine">
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment</c>, unexpanded.
    /// </param>
    /// <param name="user"><c>HKCU\Environment</c>, unexpanded.</param>
    public static EnvironmentBlock Startup(
        IReadOnlyDictionary<string, string> process,
        IReadOnlyDictionary<string, string> machine,
        IReadOnlyDictionary<string, string> user)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(user);

        // No override set yet: this composition is what the overrides are detected against.
        var registry = Compose(machine, user, process, NoOverrides);
        var overridden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in process)
        {
            // A searched variable is never an override: a refresh adds to what the process has
            // rather than replacing it, so nothing the launching shell put on PATH is at risk and
            // there is nothing to protect it from.
            if (Searched.Contains(name))
            {
                continue;
            }

            if (!registry.TryGetValue(name, out var composed) || !string.Equals(composed, value, StringComparison.Ordinal))
            {
                overridden.Add(name);
            }
        }

        return new EnvironmentBlock(
            process,
            overridden,
            Split(Value(process, "Path")),
            Split(Value(process, "PATHEXT") ?? DefaultPathExtensions));
    }

    /// <summary>
    /// The environment as it now stands: the registry read again, over the block this process
    /// started with.
    ///
    /// <para><b>Always called on the start-up block, never on a previous refresh.</b> Each result
    /// is the process's own environment composed with the registry as it is at that moment, so a
    /// directory an installer added between two passes appears at the first of them and goes again
    /// once the registry stops naming it. Chaining refreshes would instead accumulate every state
    /// the machine had passed through.</para>
    ///
    /// <para>What a refresh cannot take away is a directory the process already had at start-up.
    /// Those are kept unconditionally, so removing one from <c>PATH</c> while Deguffer is open has
    /// no effect until it is restarted. That is the deliberate direction: losing a directory would
    /// hide a tool, and a tool Deguffer cannot find is a tool whose folders Explore stops
    /// refusing (§7.1).</para>
    /// </summary>
    public EnvironmentBlock Refresh(
        IReadOnlyDictionary<string, string> machine,
        IReadOnlyDictionary<string, string> user)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(user);

        var registry = Compose(machine, user, _variables, _overridden);
        var variables = new Dictionary<string, string>(_variables, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in registry)
        {
            if (!_overridden.Contains(name))
            {
                variables[name] = value;
            }
        }

        // A name the process took from the registry and the registry has since lost is gone rather
        // than stale. A restarted Deguffer would not see it, and a provider still told where a cache
        // is goes on measuring, and offering to empty, a directory its tool has stopped pointing at
        // (§5.2). A name the registry never held is an override by construction, so it survives
        // this: the two cases are told apart by the set built at start-up, not guessed at here.
        foreach (var name in _variables.Keys)
        {
            if (!_overridden.Contains(name) && !Searched.Contains(name) && !registry.ContainsKey(name))
            {
                variables.Remove(name);
            }
        }

        // The start-up directories keep their places and their order, because that is the search
        // this process has been resolving commands against all along. A refresh only adds, which is
        // also the safe direction for §7.1: a tool Deguffer can find is a tool whose folders Explore
        // refuses.
        var directories = Extend(PathDirectories, Split(Value(registry, "Path")));
        var extensions = Extend(PathExtensions, Split(Value(registry, "PATHEXT")));

        // A searched variable has one value, not two. The loop above took it from the registry alone,
        // which is a shorter list than the one actually searched, so it is written back here from
        // the search itself — otherwise GetEnvironmentVariable("PATH") would name directories that
        // FindExecutable does not visit, and omit the ones it does.
        Join(variables, "Path", directories);
        Join(variables, "PATHEXT", extensions);

        return new EnvironmentBlock(variables, _overridden, directories, extensions);
    }

    /// <summary>The value of a variable, or null when nothing names it.</summary>
    public string? Value(string name) => Value(_variables, name);

    /// <summary>
    /// The machine and user values, joined as Windows joins them and with every <c>%NAME%</c>
    /// resolved.
    /// </summary>
    /// <param name="fallback">
    /// Where a name the registry does not hold is looked up. It holds the logon-time variables —
    /// <c>USERPROFILE</c>, <c>SystemRoot</c>, <c>SystemDrive</c> — which live in neither key and
    /// which a great many <c>PATH</c> entries are written in terms of.
    /// </param>
    /// <param name="overridden">
    /// The names <paramref name="fallback"/> answers for even where the registry has them, so an
    /// entry written as <c>%CARGO_HOME%\bin</c> follows the same value <c>CARGO_HOME</c> itself
    /// reports rather than diverging from it.
    /// </param>
    private static Dictionary<string, string> Compose(
        IReadOnlyDictionary<string, string> machine,
        IReadOnlyDictionary<string, string> user,
        IReadOnlyDictionary<string, string> fallback,
        IReadOnlySet<string> overridden)
    {
        var raw = new Dictionary<string, string>(machine, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in user)
        {
            raw[name] = Appended.Contains(name) && Value(machine, name) is { Length: > 0 } shared
                ? $"{shared.TrimEnd(Path.PathSeparator)}{Path.PathSeparator}{value}"
                : value;
        }

        // Expanded against the composed set first, so a PATH entry written as %ChocolateyInstall%\bin
        // follows a change made to ChocolateyInstall in the same visit rather than the value this
        // process was started with.
        return raw.ToDictionary(
            pair => pair.Key,
            pair => Expand(pair.Value, raw, fallback, overridden),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string Expand(
        string value,
        IReadOnlyDictionary<string, string> raw,
        IReadOnlyDictionary<string, string> fallback,
        IReadOnlySet<string> overridden)
    {
        for (var pass = 0; pass < ExpansionPasses && value.Contains('%'); pass++)
        {
            var expanded = ExpandOnce(value, raw, fallback, overridden);

            if (string.Equals(expanded, value, StringComparison.Ordinal))
            {
                return value;
            }

            value = expanded;
        }

        return value;
    }

    private static string ExpandOnce(
        string value,
        IReadOnlyDictionary<string, string> raw,
        IReadOnlyDictionary<string, string> fallback,
        IReadOnlySet<string> overridden)
    {
        var builder = new StringBuilder(value.Length);
        var index = 0;

        while (index < value.Length)
        {
            var open = value.IndexOf('%', index);
            var close = open < 0 ? -1 : value.IndexOf('%', open + 1);

            if (close < 0)
            {
                builder.Append(value, index, value.Length - index);
                break;
            }

            builder.Append(value, index, open - index);

            var name = value[(open + 1)..close];
            var resolved = name.Length == 0
                ? null
                : overridden.Contains(name) ? Value(fallback, name) : Value(raw, name) ?? Value(fallback, name);

            // A name nothing resolves stays exactly as it was written, which is what
            // ExpandEnvironmentStrings does: dropping it would turn an unresolved entry into a
            // relative path, and a relative path on PATH matches against the current directory.
            builder.Append(resolved ?? value[open..(close + 1)]);

            index = close + 1;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Write a searched list back as the variable it came from, unless there is nothing to write:
    /// a variable nothing names must keep reading as unset rather than as empty, because a caller
    /// treats the two differently.
    /// </summary>
    private static void Join(Dictionary<string, string> variables, string name, IReadOnlyList<string> entries)
    {
        if (entries.Count > 0)
        {
            variables[name] = string.Join(Path.PathSeparator, entries);
        }
    }

    private static string? Value(IReadOnlyDictionary<string, string> variables, string name) =>
        variables.TryGetValue(name, out var value) ? value : null;

    private static List<string> Split(string? value) =>
        [.. (value ?? string.Empty).Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// <paramref name="held"/>, then everything in <paramref name="added"/> it does not already
    /// have. A trailing separator is not a different directory, so it does not make a duplicate
    /// entry.
    /// </summary>
    private static List<string> Extend(IReadOnlyList<string> held, IReadOnlyList<string> added)
    {
        var known = new HashSet<string>(held.Select(Normalised), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(held);

        foreach (var entry in added)
        {
            if (known.Add(Normalised(entry)))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static string Normalised(string entry) =>
        entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
