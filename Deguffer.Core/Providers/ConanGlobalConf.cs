using System.Text;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// Which package store Conan's configuration in a home names, read from the files themselves before
/// Conan is started: <c>load_global_conf</c> and <c>ConfDefinition.loads</c> in
/// <c>conan/internal/model/conf.py</c>, and <c>PkgCache.__init__</c> in
/// <c>conan/internal/cache/cache.py</c>, Conan 2.34.0 (commit
/// <c>70b4cf60b5787ebf1a1af16ac45a11491be0d4c3</c>).
///
/// <para><b>Why not ask Conan first.</b> Every Conan command, <c>conan config home</c> included,
/// builds a <c>PkgCache</c>, which makes the folder <c>core.cache:storage_path</c> names and creates
/// <c>cache.sqlite3</c> in it. Asking Conan where its store is would write into that folder before
/// Deguffer could judge it, so a store set to the profile would gain a database in the profile. The
/// store is judged from this reading, and Conan's own answer must then agree with it.</para>
///
/// <para><b>What decides the store.</b> <c>global.conf</c>, then <c>global_user.conf</c>, whose
/// settings win, each rendered as a Jinja template with the home as its loader's folder. Nothing on
/// Conan's command line or in its environment sets a <c>core</c> setting where Deguffer starts it.
/// No setting is the home's <c>p</c>, and so is an empty value or <c>None</c>, both of which Conan
/// reads as nothing.</para>
///
/// <para><b>Only a plain reading is used.</b> A file holding a Jinja construct names whatever the
/// template renders, which only running it can say, so it is not read at all. A line that mentions
/// the setting is used only where it is the setting's own name, a plain <c>=</c>, and a value that is
/// empty, <c>None</c> or one full path, which Python's <c>eval</c> cannot parse and Conan therefore
/// keeps as written. Anything else, an append or unset, a package pattern, a value Conan would
/// evaluate, refuses the whole reading rather than guessing what Conan makes of it.</para>
/// </summary>
internal static class ConanGlobalConf
{
    public const string FileName = "global.conf";

    public const string UserFileName = "global_user.conf";

    /// <summary>Far past any configuration a person writes, and small enough to read whole.</summary>
    private const int MaximumBytes = 1024 * 1024;

    private const string Setting = ConanConfigOutput.StoragePathSetting;

    /// <summary>The operators <c>ConfDefinition.loads</c> tries before a plain <c>=</c>, in its order.</summary>
    private static readonly string[] OtherOperators = ["+=", "=+", "=!", "=~", "*="];

    /// <summary>The delimiters of Jinja's statements, expressions and comments, which no plain file holds.</summary>
    private static readonly string[] TemplateDelimiters = ["{{", "{%", "{#"];

    /// <summary>What Python's <c>str.isspace</c> is true for: Unicode's white space and the four information separators.</summary>
    private const string PythonWhiteSpace =
        "\u0009\u000a\u000b\u000c\u000d\u001c\u001d\u001e\u001f\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003"
        + "\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000";

    /// <summary>What Python's <c>str.splitlines</c> splits on.</summary>
    private static readonly char[] LineBreaks = ['\u000a', '\u000d', '\u000b', '\u000c', '\u001c', '\u001d', '\u001e', '\u0085', '\u2028', '\u2029'];

    /// <summary>The store the configuration in <paramref name="home"/> names, and the files it was read from.</summary>
    public static ConanConfReading Read(string home)
    {
        var global = Path.Combine(home, FileName);
        var user = Path.Combine(home, UserFileName);

        if (Contents(global) is not { } globalFile)
        {
            return Unreadable(global);
        }

        if (Contents(user) is not { } userFile)
        {
            return Unreadable(user);
        }

        var files = new ConanConfFiles(globalFile.Bytes, userFile.Bytes);
        Definition? named = null;

        foreach (var (path, bytes) in new[] { (global, globalFile.Bytes), (user, userFile.Bytes) })
        {
            if (bytes is not { } content)
            {
                continue;
            }

            switch (StorageSetting(path, content))
            {
                case Verdict.Refused refused:
                    return new ConanConfReading.Unusable(refused.Why);

                case Verdict.Sets sets:
                    named = sets.Value;
                    break;
            }
        }

        return new ConanConfReading.Plain(
            named is { Folder: { } folder } ? folder : Path.Combine(home, ConanConfigOutput.DefaultStoreFolder),
            files);
    }

    /// <summary>
    /// The file's bytes, a reading of no file where there is none, which Conan reads as no settings,
    /// or null where Windows would not say or the file would not be read whole.
    /// </summary>
    private static FileContents? Contents(string path) => LongPath.ProbeFile(path) switch
    {
        PathPresence.Absent => new FileContents(null),
        PathPresence.Present when BoundedFile.ReadVerbatim(path, MaximumBytes) is { } bytes => new FileContents(bytes.ToArray()),
        _ => null,
    };

    private static ConanConfReading.Unusable Unreadable(string path) => new(
        $"Deguffer could not read {path}, which tells Conan where its package store is, or it is larger "
        + $"than {MaximumBytes / 1024 / 1024} MB, so which folder Conan would use is not known.");

    /// <summary>What one file says about the setting: nothing, a value, or why it cannot be read here.</summary>
    private static Verdict? StorageSetting(string path, byte[] content)
    {
        string text;

        try
        {
            text = BoundedFile.StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return new Verdict.Refused($"{path} is not UTF-8 text, the only encoding Conan reads it in, so which package store it names cannot be known.");
        }

        if (TemplateDelimiters.Any(delimiter => text.Contains(delimiter, StringComparison.Ordinal)))
        {
            return new Verdict.Refused(
                $"{path} is a template, which Conan renders before it reads it, so which package store it "
                + "names cannot be known without running Conan.");
        }

        Verdict? verdict = null;
        var number = 0;

        foreach (var raw in text.Split(LineBreaks))
        {
            number++;
            var line = PythonStrip(raw);

            if (line.Length == 0 || line.StartsWith('#') || !line.Contains("storage_path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (PlainValue(line) is not { } value)
            {
                return new Verdict.Refused(
                    $"Line {number} of {path} sets {Setting} in a way Deguffer does not read as one plain "
                    + "folder, so which package store Conan would use is not known.");
            }

            // A later line wins, as each line Conan reads replaces the setting.
            verdict = new Verdict.Sets(value);
        }

        return verdict;
    }

    /// <summary>
    /// The setting a line defines, its folder null where the value is empty or <c>None</c>, or null
    /// where the line is anything but a plain definition of the setting to one full path.
    /// </summary>
    private static Definition? PlainValue(string line)
    {
        if (OtherOperators.Any(op => line.Contains(op, StringComparison.Ordinal)) || line.IndexOf('=') is not (> 0 and var equals))
        {
            return null;
        }

        if (PythonStrip(line[..equals]) != Setting)
        {
            return null;
        }

        var value = PythonStrip(line[(equals + 1)..]);

        return value switch
        {
            "" or "None" => new Definition(null),
            _ when Path.IsPathFullyQualified(value) && LongPath.Configured(value) is { } folder => new Definition(folder),
            _ => null,
        };
    }

    /// <summary>Python's <c>str.strip</c>, which takes the information separators as well as what .NET calls white space.</summary>
    private static string PythonStrip(string text) => text.AsSpan().Trim(PythonWhiteSpace).ToString();

    private readonly record struct FileContents(byte[]? Bytes);

    /// <summary>The store one definition of the setting names, null for the home's default.</summary>
    private readonly record struct Definition(string? Folder);

    private abstract record Verdict
    {
        public sealed record Sets(Definition Value) : Verdict;

        public sealed record Refused(string Why) : Verdict;
    }
}

/// <summary>What <see cref="ConanGlobalConf.Read"/> came to.</summary>
internal abstract record ConanConfReading
{
    /// <summary>The store the configuration names, in full form, and the files that named it.</summary>
    public sealed record Plain(string Store, ConanConfFiles Files) : ConanConfReading;

    /// <summary>Which store Conan would use cannot be known without running it, for the reason given.</summary>
    public sealed record Unusable(string Why) : ConanConfReading;
}

/// <summary>
/// The bytes of the home's <c>global.conf</c> and <c>global_user.conf</c> as a plan read them, each
/// null where there was no such file, so the clean can tell whether either has changed since.
/// </summary>
internal sealed class ConanConfFiles(byte[]? global, byte[]? user)
{
    private readonly byte[]? _global = global;
    private readonly byte[]? _user = user;

    /// <summary>Whether <paramref name="other"/> holds the same files, byte for byte.</summary>
    public bool SameAs(ConanConfFiles other) => Same(_global, other._global) && Same(_user, other._user);

    private static bool Same(byte[]? one, byte[]? two) =>
        one is null ? two is null : two is not null && one.AsSpan().SequenceEqual(two);
}
