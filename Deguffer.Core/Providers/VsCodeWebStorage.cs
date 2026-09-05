using System.Text.RegularExpressions;
using Deguffer.Core.Execution;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What one Code - OSS editor's <c>WebStorage</c> directory holds: one directory per webview
/// storage partition, and whatever else is in there.
///
/// <para>Its own type because it answers its own question, the same split
/// <see cref="VsCodeUserDataDiscovery"/> exists for one level up. This reads a directory and says
/// what is in it; <see cref="VsCodeCacheProvider"/> decides what that means. Keeping them apart is
/// what lets the reading be memoised and asked three times — by presence, by planning and by the
/// §7.1 declaration — while each §5.6 assertion stays a statement about one run.</para>
/// </summary>
/// <param name="Numbered">The webview storage partitions, by name.</param>
/// <param name="Unrecognised">
/// Directories in there that are not partitions. Kept rather than dropped: each is a sibling of a
/// directory the provider descends into, which is exactly when an over-broad rule takes both.
/// </param>
/// <param name="Links">
/// Links in there, which are never followed. Named rather than dropped, because a link under a
/// directory Deguffer is working in is a child the user can see.
/// </param>
/// <param name="Unreadable">
/// The directory refused to be listed. It exists — a full path resolves through a directory the
/// account may not list — so this is not the same as holding no partitions.
/// </param>
internal readonly partial record struct VsCodeWebStorage(
    IReadOnlyList<string> Numbered,
    IReadOnlyList<string> Unrecognised,
    IReadOnlyList<string> Links,
    bool Unreadable)
{
    /// <summary>The directory the per-webview storage partitions sit in.</summary>
    public const string DirectoryName = "WebStorage";

    private static readonly VsCodeWebStorage Nothing = new([], [], [], Unreadable: false);

    /// <summary>
    /// A webview storage partition: digits and nothing else, on the pattern
    /// <see cref="ChromiumUserDataDiscovery"/> uses for a numbered profile. Chromium names these
    /// after its own partition identifiers, so a directory in here with any other name is something
    /// Deguffer has not identified and is never looked inside.
    ///
    /// Anchored with <c>\z</c> rather than <c>$</c>: <c>$</c> also matches before a trailing
    /// newline, and a check that decides whether a directory may be entered should admit no such
    /// reading.
    /// </summary>
    [GeneratedRegex(@"\A[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PartitionName();

    /// <summary>
    /// Read the <c>WebStorage</c> directory under <paramref name="folder"/>.
    ///
    /// <para>A <c>WebStorage</c> that is itself a link yields nothing here and is left to
    /// <see cref="CacheLevelWalk"/>, which meets it as a link child of the folder and names it once.
    /// Declining it in both places would report one skipped directory as two.</para>
    /// </summary>
    public static VsCodeWebStorage Of(string folder)
    {
        var webStorage = Path.Combine(folder, DirectoryName);

        if (!LongPath.DirectoryExists(webStorage) || LongPath.IsReparsePoint(webStorage))
        {
            return Nothing;
        }

        var scan = ChildDirectories.Under(webStorage);

        if (scan.Unreadable)
        {
            return Nothing with { Unreadable = true };
        }

        return new VsCodeWebStorage(
            [.. scan.Directories.Where(d => PartitionName().IsMatch(d.Name)).Select(d => d.Name)],
            [.. scan.Directories.Where(d => !PartitionName().IsMatch(d.Name)).Select(d => d.Name)],
            [.. scan.Links.Select(d => d.Name)],
            Unreadable: false);
    }

    /// <summary>
    /// Record what this reading leaves alone, and answer how many things that was.
    ///
    /// <para>A partition is asserted to survive even though it is entered, for the reason the
    /// <c>WebStorage</c> classification gives: the directory really is left standing and something
    /// inside it really is being removed, so the generic "we did not recognise that" wording would
    /// be false about it.</para>
    /// </summary>
    public int Spared(
        string folder,
        ICollection<(string Path, string Reason)> survivors,
        ICollection<(string Path, string Reason)> declined,
        ICollection<PlanNote> notes)
    {
        var webStorage = Path.Combine(folder, DirectoryName);

        foreach (var partition in Numbered)
        {
            survivors.Add((
                Path.Combine(webStorage, partition),
                "One webview's storage. Only the web cache inside it is removed, and what that view "
                + "saved stays."));
        }

        foreach (var other in Unrecognised)
        {
            survivors.Add((
                Path.Combine(webStorage, other),
                "Not a webview storage partition Deguffer recognises, so it is left alone and never "
                + "looked inside."));
        }

        foreach (var link in Links)
        {
            var path = Path.Combine(webStorage, link);
            notes.Add(CacheLevelWalk.Note(path));
            declined.Add((path, CacheLevelWalk.LinkReason));
        }

        return Numbered.Count + Unrecognised.Count;
    }
}
