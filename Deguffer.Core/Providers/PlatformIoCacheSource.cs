namespace Deguffer.Core.Providers;

/// <summary>
/// How Deguffer found PlatformIO's cache directory. Each one licenses a different sentence: a report,
/// a documented folder inside a reported core directory, or a guess that a moved install will not
/// match.
/// </summary>
internal enum PlatformIoCacheSource
{
    /// <summary>PlatformIO named its cache directory.</summary>
    Reported,

    /// <summary>
    /// PlatformIO named its core directory but not its cache directory, which is what 6.1.19 does, so
    /// the cache directory is the documented one inside that core directory.
    /// </summary>
    InReportedCore,

    /// <summary>PlatformIO named neither, so the cache directory is the documented default.</summary>
    Default,
}
