namespace Deguffer.Core.Providers;

/// <summary>A folder a symbol path has debuggers copy symbols into, and where those symbols come from.</summary>
/// <param name="Folder">The folder as the path spells it, or null for the debugger's default store.</param>
/// <param name="Sources">
/// Every server, share or folder whose symbols are copied in. Never empty: an element with nothing to copy
/// from makes no store.
/// </param>
public sealed record SymbolPathStore(string? Folder, IReadOnlyList<string> Sources);

/// <summary>What one symbol path says about the folders on this machine.</summary>
/// <param name="Stores">The folders debuggers copy symbols into, in the order the path names them.</param>
/// <param name="Masters">
/// Every share or folder the path reads symbols from without copying them anywhere first: the last store
/// of a chain, and a plain folder. Each is somebody's own symbols, never a copy.
/// </param>
public sealed record SymbolPathReading(IReadOnlyList<SymbolPathStore> Stores, IReadOnlyList<string> Masters);

/// <summary>
/// Reads a symbol path, the value of <c>_NT_SYMBOL_PATH</c> or <c>_NT_ALT_SYMBOL_PATH</c>, as Microsoft's
/// "Advanced SymSrv Use" and "Symbol path" pages describe it.
///
/// <para>Elements are separated by semicolons. A <c>srv*</c> element, short for
/// <c>symsrv*symsrv.dll*</c>, names a chain of stores separated by asterisks: the last is the one the
/// symbols come from, and each to its left is a downstream store that symsrv copies every file it finds
/// into. An empty store, two asterisks in a row or one at the end, is the default store. A chain of one
/// server reached over HTTP still copies, into the default store. A <c>cache*</c> element names a folder
/// that keeps a copy of whatever any element to its right loads. Anything else is a folder searched as it
/// stands.</para>
///
/// <para>A bare <c>srv*</c> is what <c>.symfix</c> writes, and means Microsoft's server and the default
/// store.</para>
/// </summary>
public static class SymbolPath
{
    /// <summary>Microsoft's own server, which a bare <c>srv*</c> names.</summary>
    public const string MicrosoftServer = "https://msdl.microsoft.com/download/symbols";

    public static SymbolPathReading Read(string? value)
    {
        var elements = (value ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(element => element.Trim('"').Trim())
            .Where(element => element.Length > 0)
            .ToList();

        List<SymbolPathStore> stores = [];
        List<string> masters = [];

        // A cache element keeps whatever the elements to its right load, so the sources are gathered from
        // the right, and each cache element takes those gathered so far.
        List<string> loadedToTheRight = [];

        for (var i = elements.Count - 1; i >= 0; i--)
        {
            var element = elements[i];

            if (Prefixed(element, "cache*") is { } cache)
            {
                if (loadedToTheRight.Count > 0)
                {
                    stores.Add(new SymbolPathStore(Folder(cache), [.. loadedToTheRight.Distinct(StringComparer.OrdinalIgnoreCase)]));
                }

                continue;
            }

            if (Chain(element) is { } chain)
            {
                var (downstream, source) = Split(chain);

                if (source.Length == 0)
                {
                    continue;
                }

                if (!IsServer(source))
                {
                    masters.Add(source);
                }

                stores.AddRange(downstream.Reverse().Select(store => new SymbolPathStore(store, [source])));
                loadedToTheRight.Add(source);
                continue;
            }

            masters.Add(element);
            loadedToTheRight.Add(element);
        }

        // Gathered from the right, so turned back into the order the path names them.
        stores.Reverse();
        masters.Reverse();

        return new SymbolPathReading(stores, masters);
    }

    /// <summary>
    /// The stores of a <c>srv*</c> or <c>symsrv*&lt;dll&gt;*</c> element, or null where the element is
    /// neither.
    /// </summary>
    private static IReadOnlyList<string>? Chain(string element)
    {
        if (Prefixed(element, "srv*") is { } stores)
        {
            return [.. stores.Split('*').Select(store => store.Trim())];
        }

        if (Prefixed(element, "symsrv*") is { } named
            && named.IndexOf('*') is var afterLibrary and >= 0)
        {
            return [.. named[(afterLibrary + 1)..].Split('*').Select(store => store.Trim())];
        }

        return null;
    }

    /// <summary>
    /// The downstream stores of a chain, null for the default one, and the store they are filled from.
    /// </summary>
    private static (IReadOnlyList<string?> Downstream, string Source) Split(IReadOnlyList<string> chain)
    {
        if (chain is [""])
        {
            return ([null], MicrosoftServer);
        }

        var source = chain[^1];

        if (chain.Count == 1)
        {
            // symsrv always keeps what it fetches over HTTP, in the default store where none is named.
            return (IsServer(source) ? [null] : [], source);
        }

        return ([.. chain.Take(chain.Count - 1).Select(Folder)], source);
    }

    private static string? Prefixed(string element, string prefix) =>
        element.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? element[prefix.Length..] : null;

    /// <summary>A folder as the path names it, or null for an empty one, which is the default store.</summary>
    private static string? Folder(string store) => store.Trim() is { Length: > 0 } named ? named : null;

    private static bool IsServer(string store) =>
        store.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || store.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
