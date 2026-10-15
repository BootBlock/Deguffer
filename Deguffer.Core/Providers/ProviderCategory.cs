namespace Deguffer.Core.Providers;

/// <summary>
/// What kind of thing a provider's location belongs to, so a reader choosing which locations to scan
/// can take a whole kind at once. §7 groups by cause, and these are the causes a user recognises
/// before they recognise a tool: "my games", "my browsers", "the build output in my projects".
///
/// <para>A provider declares its own, through <see cref="ProviderDescription.Category"/>, so a new
/// provider cannot be added without one and nothing else keeps a list of providers to fall out of
/// step with.</para>
///
/// <para>It says nothing about what may be deleted. That is <see cref="Safety.SafetyTier"/>'s, and a
/// category holds locations of every tier: the Recycle Bin and the Delivery Optimization cache are
/// both Windows.</para>
///
/// <para>The values are ordinal, and a list of categories is drawn in this order: the places every
/// machine has first, then what an ordinary user installs, then what a developer does.</para>
/// </summary>
public enum ProviderCategory
{
    /// <summary>The temporary folders, and what tools leave in them.</summary>
    TemporaryFiles = 0,

    /// <summary>What Windows itself and the graphics driver keep: updates, restore points, crash dumps, shader caches.</summary>
    Windows = 1,

    /// <summary>Installers and superseded versions that applications and drivers leave behind after updating.</summary>
    InstallersAndUpdates = 2,

    /// <summary>Web browsers and the applications built on a browser engine.</summary>
    Browsers = 3,

    /// <summary>Game launchers, their caches and emulators.</summary>
    Games = 4,

    /// <summary>Photo, video, music and media server applications.</summary>
    MediaAndCreative = 5,

    /// <summary>Package managers' shared caches, and the tools a package manager downloads.</summary>
    PackageCaches = 6,

    /// <summary>What a build leaves inside a project's own folder, found under the approved source folders.</summary>
    ProjectBuildOutput = 7,

    /// <summary>Editors, language servers, AI coding tools and the virtual disks development runs in.</summary>
    DeveloperTools = 8,
}
