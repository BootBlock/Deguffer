using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What Plex Media Server's transcoder and photo transcoder leave on disk. See
/// <see cref="MediaServerTranscodeProvider"/> for the plan and <see cref="PlexServerLayout"/> for
/// where Plex keeps them.
/// </summary>
public sealed class PlexTranscodeProvider : MediaServerTranscodeProvider
{
    public PlexTranscodeProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
    }

    public override string Id => "plex-transcode";

    public override string Name => "Plex Media Server transcoder files";

    public override string WhatHappensOnNextUse =>
        "Plex converts a film or an episode again the next time a player that cannot play the original "
        + "asks for it, and resizes posters and thumbnails again as they are shown. Your libraries, watch "
        + "history, ratings and downloads waiting to go to a phone are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Plex Media Server",
        Publisher = "Plex",
        Purpose = "When a player cannot play a file as it is, Plex converts it as it streams and writes "
            + "the parts to a folder of its own. A stream that ends badly leaves its parts there for good. "
            + "Plex also keeps the posters and thumbnails it resized for its apps.",
        Recommendation = "Deguffer removes the transcoder's leftovers and the resized pictures, and "
            + $"nothing written in the last {QuietHours} hours, so a film playing now is not cut off. It "
            + "reads Plex's settings to find the folders, and never touches your database, your "
            + "artwork or media waiting to go to a phone.",
    };

    protected override string NothingHere => "Plex Media Server has left no transcoder files in this user's account.";

    protected override string ServerName => "Plex";

    protected override IReadOnlyList<string> ConflictingProcessNames => PlexServerLayout.ProcessNames;

    protected override MediaServerLayout FindLayout() => PlexServerLayout.Find(Environment);
}
