using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// What Jellyfin's transcoder leaves on disk. See <see cref="MediaServerTranscodeProvider"/> for the
/// plan and <see cref="JellyfinServerLayout"/> for where Jellyfin keeps it.
/// </summary>
public sealed class JellyfinTranscodeProvider : MediaServerTranscodeProvider
{
    private readonly ISystemDirectories _system;

    public JellyfinTranscodeProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        ISystemDirectories? system = null)
        : base(
            environment ?? UserEnvironment.Current,
            runner ?? ProcessRunner.Default,
            inspector ?? ProcessInspector.Default,
            scanner ?? DirectoryScanner.Default)
    {
        _system = system ?? SystemDirectories.Current;
    }

    public override string Id => "jellyfin-transcode";

    public override string Name => "Jellyfin transcoder files";

    public override string WhatHappensOnNextUse =>
        "Jellyfin converts a film or an episode again the next time a player that cannot play the "
        + "original asks for it. Your libraries, users, watch history, settings and backups are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Application = "Jellyfin",
        Publisher = "the Jellyfin project",
        Purpose = "When a player cannot play a file as it is, Jellyfin converts it as it streams and "
            + "writes the parts to a folder of its own. A stream that ends badly leaves its parts there "
            + "until the server next starts or its daily clean-up runs.",
        Recommendation = "Deguffer removes the transcoder's leftovers, and nothing written in the last "
            + $"{QuietHours} hours, which is the rule Jellyfin's own clean-up follows. It removes nothing "
            + "from a folder unless Jellyfin's own marker file shows the folder is Jellyfin's.",
    };

    protected override string NothingHere => "Jellyfin has left no transcoder files on this machine.";

    protected override string ServerName => "Jellyfin";

    protected override IReadOnlyList<string> ConflictingProcessNames => JellyfinServerLayout.ProcessNames;

    protected override MediaServerLayout FindLayout() => JellyfinServerLayout.Find(Environment, _system);
}
