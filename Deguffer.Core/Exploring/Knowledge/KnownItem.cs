namespace Deguffer.Core.Exploring.Knowledge;

/// <summary>
/// What an entry's <see cref="KnownItem.RelativePath"/> is measured from.
///
/// <para>A place rather than a literal path, because none of these is at a fixed address.
/// <c>%SystemRoot%</c> is <c>C:\Windows</c> on nearly every machine and is not guaranteed to be;
/// a profile can sit on another drive; <c>%LOCALAPPDATA%</c> can be redirected away from the
/// profile it is normally inside. Every one of them is read through
/// <see cref="Safety.ISystemDirectories"/> or <see cref="Safety.IUserEnvironment"/>, which is both
/// the correct answer and what lets the whole catalogue be asserted against a synthetic
/// profile.</para>
/// </summary>
public enum KnownPlace
{
    /// <summary>
    /// The top of whichever volume the path is on, so one entry covers every drive. This is where
    /// NTFS keeps its own records and where Windows keeps the paging file, the recycle bin and the
    /// restore points — on <c>C:</c> and on a data disk plugged in a moment ago alike.
    ///
    /// <para>The relative path may go deeper than one segment, because NTFS's own optional features
    /// live a level down in <c>$Extend</c>.</para>
    /// </summary>
    VolumeRoot,

    /// <summary><c>%SystemRoot%</c>, ordinarily <c>C:\Windows</c>.</summary>
    WindowsDirectory,

    /// <summary><c>%PROGRAMFILES%</c>, the 64-bit one.</summary>
    ProgramFiles,

    /// <summary><c>%ProgramFiles(x86)%</c>.</summary>
    ProgramFilesX86,

    /// <summary><c>%PROGRAMDATA%</c>, ordinarily <c>C:\ProgramData</c>.</summary>
    ProgramData,

    /// <summary>The folder holding every account's profile, ordinarily <c>C:\Users</c>.</summary>
    UserProfiles,

    /// <summary><c>%USERPROFILE%</c> — the signed-in account's own profile, and nobody else's.</summary>
    UserProfile,

    /// <summary><c>%LOCALAPPDATA%</c>.</summary>
    LocalAppData,

    /// <summary><c>%APPDATA%</c>, the roaming one.</summary>
    RoamingAppData,

    /// <summary>
    /// <c>%USERPROFILE%\AppData\LocalLow</c>.
    ///
    /// <para>Unlike the other two tiers this one can be absent from the anchor table, because the
    /// platform call behind it can decline to say where it is. An entry anchored here then explains
    /// nothing, which is the right answer: the alternative is a path built by assumption.</para>
    /// </summary>
    LocalLowAppData,

    /// <summary>
    /// Anywhere at all, matched on the name alone.
    ///
    /// <para>Reserved for names that mean one thing wherever they are found. <c>node_modules</c> is
    /// a package tree in every checkout on the disk, and saying so does not depend on knowing which
    /// checkout. A name whose meaning changes with its parent does not belong here — it belongs
    /// under the place that gives it that meaning, or nowhere.</para>
    /// </summary>
    Anywhere,
}

/// <summary>
/// One entry of the reference the Explore page reads from: what a well-known file or folder is, and
/// whether deleting it recovers any space.
///
/// <para>It exists because a size picture is very good at showing that something is large and says
/// nothing at all about what it is. The reader is left to search the web for a name they found on
/// their own disk, and the answers there are frequently wrong in the one direction that costs
/// something. So the app carries what it knows.</para>
///
/// <para>In Core, next to <see cref="Acting.ExploreActionPolicy"/> and for the same reason: what
/// Deguffer tells somebody about <c>C:\Windows</c> has to be assertable without a WinUI host, and
/// text that only exists inside a XAML tooltip is text nothing can check.</para>
/// </summary>
/// <param name="Place">Where <paramref name="RelativePath"/> starts from.</param>
/// <param name="RelativePath">
/// Where this sits below <paramref name="Place"/>, using <c>\</c> between segments. Empty means the
/// place itself, which is how <c>C:\Windows</c> and <c>C:\ProgramData</c> are described. For
/// <see cref="KnownPlace.Anywhere"/> it is a single name and nothing else.
/// </param>
/// <param name="Summary">
/// What the thing is and what it is for, in a few plain sentences. Written for somebody who has just
/// found the name on their own disk and does not know it, so it says what would happen without it
/// rather than restating the name in longer words.
/// </param>
/// <param name="Removal">
/// Whether deleting it recovers space, as <b>one sentence on one line</b>. The page puts it last,
/// alone, after a blank line, because it is the question the reader actually came with — and a
/// verdict buried in a paragraph is a verdict somebody skims past.
///
/// <para>Where Windows or the tool itself offers a supported way to reclaim the space, this names
/// it. §5.1 makes that the preferred route for a deletion Deguffer performs, and the same reasoning
/// applies to one the reader is about to perform by hand.</para>
/// </param>
public sealed record KnownItem(
    KnownPlace Place,
    string RelativePath,
    string Summary,
    string Removal)
{
    /// <summary>The empty line between the parts of a tip. Shared with <see cref="KnownMatch"/>.</summary>
    internal const string Blank = "\r\n\r\n";

    /// <summary>
    /// The whole of what a hovering reader is shown, with <paramref name="facts"/> — whatever the
    /// page measured about this particular file, its dates among them — between the explanation and
    /// the verdict.
    ///
    /// <para><see cref="Removal"/> comes last, on its own, with an empty line above it. It is the
    /// question the reader arrived with, and a verdict set inside a paragraph is one they skim
    /// past. Everything before it is context for that line rather than the other way round.</para>
    ///
    /// <para>A literal <c>\r\n</c> rather than <see cref="Environment.NewLine"/>, because this
    /// string is laid out for a Windows control to render and the assertion that the last line
    /// stands alone should not be an assertion about the host operating system.</para>
    /// </summary>
    /// <param name="facts">
    /// Omitted, or empty, where the page has nothing of its own to add — which is the map, where
    /// the size and the date are already on the status line under the picture.
    /// </param>
    public string Tip(string? facts = null) =>
        string.IsNullOrWhiteSpace(facts)
            ? Summary + Blank + Removal
            : Summary + Blank + facts.Trim() + Blank + Removal;
}
