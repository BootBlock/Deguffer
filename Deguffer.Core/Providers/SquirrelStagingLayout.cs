using System.Text.RegularExpressions;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// What the Squirrel updater's shared staging folder holds, as the Squirrel row plans it and as
/// §7.1 reads it from outside.
/// </summary>
internal static partial class SquirrelStagingLayout
{
    public const string UnpackedReason =
        "An install or an update the Squirrel updater unpacked here and did not clear away "
        + "afterwards. Nothing reads it once the update has finished.";

    public const string RootReason =
        "The staging folder itself must survive — only the directories Squirrel unpacked into it "
        + "are removed, and every Squirrel application on this machine shares it.";

    /// <summary>
    /// The files Squirrel and its bootstrapper leave in the staging folder, named in full rather
    /// than sampled.
    ///
    /// <para>Named at all because child classification enumerates directories, so anything that is a
    /// file is never seen, never classified and never asserted unless a provider names it. The
    /// Squirrel row removes directories from the folder those files sit in, so an assertion that the
    /// folder survived would pass with every one of them gone.</para>
    ///
    /// <para>Whichever is absent records itself as nothing to preserve rather than as a pass, so
    /// naming all three costs nothing on a machine that holds one.</para>
    /// </summary>
    public static readonly (string Name, string Reason)[] RootFiles =
    [
        ("SquirrelSetup.log", "The record of what the updater installed on this machine, and when."),
        ("Squirrel-Install.log", "The record of what the updater installed on this machine, and when."),
        ("setup.json", "What an installer was told to run. It is not something the updater unpacked."),
    ];

    /// <summary>
    /// Whether <paramref name="name"/> is a staging directory Squirrel made. See
    /// <see cref="StagingDirectory"/> for why the pattern is exactly as narrow as it is.
    /// </summary>
    public static bool IsStagingDirectory(string name) => StagingDirectory().IsMatch(name);

    /// <summary>
    /// The staging folder as §5.2 declares it: the directories Squirrel unpacked into it may go, and
    /// nothing else in it may.
    /// </summary>
    public static ToolRoot Root(string staging) => ToolRoot.Folders(
        staging,
        "This is the folder the Squirrel updater unpacks installs and updates into, and "
        + "every application on this machine that uses Squirrel shares it. Deguffer removes "
        + "the directories it unpacked and nothing else.",
        IsStagingDirectory);

    /// <summary>
    /// A staging directory Squirrel made: the literal prefix it writes, then <b>exactly one</b>
    /// character from the alphabet its name generator draws on — lowercase Latin, then Greek and
    /// Coptic, then Cyrillic, 360 characters in all.
    ///
    /// <para><b>One character rather than one or more, and that difference is the whole of §5.2
    /// here.</b> One character is the shortest suffix the generator can emit, and it hands out the
    /// first free name — so it produces one until 360 staging directories exist at once, which is
    /// what the framework's own bug reports show and what the measured machine held. This is
    /// therefore the narrowest pattern that still recognises every name a real machine holds, and
    /// narrowest is what §5.2 asks for. Past the 360th the generator starts producing longer names,
    /// and those are left alone: a longer run of Latin letters cannot be told from an ordinary word,
    /// so <c>temp</c> followed by one <em>or more</em> also claims <c>templates</c>,
    /// <c>temporary</c> and <c>tempdata</c>. Under the default root that would cost nothing, because
    /// nothing else writes there; under a <c>SQUIRREL_TEMP</c> pointed at a folder shared with
    /// anything else it would offer somebody's directory as a pre-selected Tier 1 row.</para>
    ///
    /// <para><b>It does not follow that no English word matches, and the code is right rather than
    /// the tempting reading.</b> <c>tempo</c>, <c>tempi</c>, <c>tempt</c> and <c>temps</c> are all
    /// matched, because <c>o</c>, <c>i</c>, <c>t</c> and <c>s</c> are genuinely characters the
    /// generator emits — excluding them would fail to recognise a directory Squirrel really made,
    /// which is the wrong direction. What the narrowing buys is the <em>bound</em>, not a word
    /// list.</para>
    ///
    /// <para>Case-sensitive, deliberately. Squirrel generates these names in lowercase and Windows
    /// keeps the case a directory was created with, so matching case exactly recognises every folder
    /// Squirrel made and nothing else.</para>
    /// </summary>
    [GeneratedRegex(@"\Atemp[a-z\u03B0-\u03FE\u0400-\u04FE]\z", RegexOptions.CultureInvariant)]
    private static partial Regex StagingDirectory();
}
