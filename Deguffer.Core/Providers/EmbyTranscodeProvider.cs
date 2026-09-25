using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What Emby Server's transcoder leaves on disk. See <see cref="MediaServerTranscodeProvider"/> for
/// the plan and <see cref="EmbyServerLayout"/> for where Emby keeps it.
/// </summary>
public sealed class EmbyTranscodeProvider : MediaServerTranscodeProvider
{
    public EmbyTranscodeProvider(
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

    public override string Id => "emby-transcode";

    public override string Name => "Emby Server transcoder files";

    public override string WhatHappensOnNextUse =>
        "Emby converts a film or an episode again the next time a player that cannot play the original "
        + "asks for it. Your libraries, users, watch history and settings are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Emby Server",
        Publisher = "Emby",
        Purpose = "When a player cannot play a file as it is, Emby converts it as it streams and writes "
            + "the parts to a folder of its own. A stream that ends badly leaves its parts there until "
            + "the server next starts.",
        Recommendation = "Deguffer removes the transcoder's leftovers, and nothing written in the last "
            + $"{QuietHours} hours, so a film playing now is not cut off. It reads Emby's settings to find "
            + "the folder, and removes only what is inside the transcoding-temp folder Emby made.",
    };

    protected override string NothingHere => "Emby Server has left no transcoder files in this user's account.";

    protected override string ServerName => "Emby";

    protected override IReadOnlyList<string> ConflictingProcessNames => EmbyServerLayout.ProcessNames;

    protected override MediaServerLayout FindLayout() => EmbyServerLayout.Find(Environment);
}
