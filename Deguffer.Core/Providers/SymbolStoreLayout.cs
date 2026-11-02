using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Deguffer.Core.Providers;

/// <summary>
/// What a debugger's symbol store holds on disk, and which of it is a downloaded copy.
///
/// <para><b>The layout is Microsoft's, from the symbol store folder tree page.</b> Every file is kept as
/// <c>&lt;store&gt;\&lt;file name&gt;\&lt;index&gt;\&lt;file name&gt;</c>, where the index is a symbol
/// file's signature and age, or an image's time stamp and size, in hexadecimal. A store with
/// <c>index2.txt</c> at its root adds a level named for the first two letters of each file name. Observed
/// in WinDbg's store: images as well as symbol files, and indexes in both letter cases.</para>
///
/// <para><b>Recognised by its whole shape, never by its name.</b> The entries are named after whatever was
/// debugged, so an entry is one only where every index under it is hexadecimal and holds nothing but the
/// file it is named for, its compressed form or a pointer to it. Anything else in the store is Tier 4.</para>
///
/// <para><b>A store holding <c>000Admin</c> is never a cache.</b> SymStore makes that folder when it builds
/// a store, and keeps its transaction history there, so it is someone's own symbol store, where a symbol
/// may be the only copy there is.</para>
/// </summary>
public static partial class SymbolStoreLayout
{
    /// <summary>The marker symsrv writes at a store's root, which says the folder is a store.</summary>
    public const string PingFile = "pingme.txt";

    /// <summary>The marker that turns a store into the two-level layout.</summary>
    public const string TwoLevelFile = "index2.txt";

    /// <summary>The marker that asks symsrv to keep pointers rather than copies in this store.</summary>
    public const string PointerRequestFile = "wantsptr.txt";

    /// <summary>The folder SymStore makes in a store it builds, holding its transaction history.</summary>
    public const string AdministrationFolder = "000Admin";

    /// <summary>The pointer symsrv may keep in an index in place of the file.</summary>
    public const string PointerFile = "file.ptr";

    /// <summary>The folder a debugger's default store is in, below its home folder.</summary>
    public const string DefaultStoreName = "sym";

    /// <summary>The variables that set a debugger's symbol path, read by every Microsoft debugger.</summary>
    public static readonly IReadOnlyList<string> PathVariables = ["_NT_SYMBOL_PATH", "_NT_ALT_SYMBOL_PATH"];

    /// <summary>The variable that moves the home folder a default store is made in.</summary>
    public const string HomeVariable = "DBGHELP_HOMEDIR";

    /// <summary>
    /// The servers Microsoft runs for public symbols: its own, which <c>.symfix</c> names, and NuGet.org's,
    /// which Visual Studio offers beside it. NuGet.org never deletes a package, so its symbols stay too.
    /// </summary>
    public static readonly FrozenSet<string> MicrosoftHosts =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "msdl.microsoft.com", "symbols.nuget.org");

    /// <summary>The markers a store keeps at its root, which survive every run.</summary>
    public static readonly FrozenSet<string> RootMarkers =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, PingFile, TwoLevelFile, PointerRequestFile);

    /// <summary>WinDbg, its engine host, and the console debuggers that share its engine.</summary>
    public static readonly IReadOnlyList<string> WinDbgProcessNames = ["windbg", "DbgX.Shell", "EngHost", "cdb", "kd", "ntsd"];

    /// <summary>Visual Studio, whose debugger runs inside it.</summary>
    public static readonly IReadOnlyList<string> VisualStudioProcessNames = ["devenv"];

    /// <summary>Every debugger that reads the symbol path variables.</summary>
    public static readonly IReadOnlyList<string> AllProcessNames = [.. WinDbgProcessNames, .. VisualStudioProcessNames];

    /// <summary>
    /// An index: a symbol file's 32-digit signature and its age, or an image's 8-digit time stamp and its
    /// size, so from 9 to 40 digits.
    /// </summary>
    [GeneratedRegex(@"\A[0-9A-Fa-f]{9,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Index();

    /// <summary>Whether <paramref name="name"/> is an index folder's name.</summary>
    public static bool IsIndex(string name) => Index().IsMatch(name);

    /// <summary>
    /// Whether <paramref name="name"/> could name a stored file: a name and an extension. A folder with no
    /// extension is never an entry, which keeps out the folders a store's owner made beside the entries.
    /// </summary>
    public static bool IsEntryName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1;
    }

    /// <summary>
    /// Whether <paramref name="file"/> may sit in an index of the entry named <paramref name="entry"/>: the
    /// file itself, its compressed form, whose last letter symsrv replaces with an underscore, or a pointer.
    /// </summary>
    public static bool IsIndexedFile(string entry, string file) =>
        file.Equals(entry, StringComparison.OrdinalIgnoreCase)
        || file.Equals(PointerFile, StringComparison.OrdinalIgnoreCase)
        || (file.Length == entry.Length
            && file[^1] == '_'
            && file.AsSpan(0, file.Length - 1).Equals(entry.AsSpan(0, entry.Length - 1), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether <paramref name="entry"/> belongs under the two-letter folder <paramref name="level"/>, whose
    /// name is the first two letters of every file kept below it. An entry always has a name and an
    /// extension, so it always has two letters to give.
    /// </summary>
    public static bool BelongsUnder(string level, string entry) =>
        level.Length == 2 && entry.StartsWith(level, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="server"/> is one of Microsoft's public symbol servers. Anything else, a share
    /// or a folder included, may hold symbols nothing else keeps.
    /// </summary>
    public static bool IsMicrosoftServer(string server) =>
        Uri.TryCreate(server, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && MicrosoftHosts.Contains(uri.Host);

    /// <summary>
    /// WinDbg's own store. Observed on current WinDbg, and named in a 2014 Microsoft field engineer's post,
    /// "Debugging with symbols: a field perspective". No current Microsoft page names it.
    /// </summary>
    public static string WinDbgStore(string programData) => Path.Combine(programData, "Dbg", DefaultStoreName);

    /// <summary>Visual Studio's store where its setting names none, observed on Visual Studio 2026.</summary>
    public static string VisualStudioDefaultStore(string tempPath) => Path.Combine(tempPath, "SymbolCache");
}
