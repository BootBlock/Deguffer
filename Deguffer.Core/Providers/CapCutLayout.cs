using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One edition of ByteDance's desktop video editor: CapCut, or Jianying Pro, the edition sold in
/// mainland China. Both keep the same tree under a folder of their own name in <c>%LOCALAPPDATA%</c>.
/// </summary>
/// <param name="Name">The edition as its users name it.</param>
/// <param name="FolderName">Its folder under <c>%LOCALAPPDATA%</c>.</param>
/// <param name="ProcessName">The editor's process, which is the launcher's name too.</param>
public sealed record CapCutEdition(string Name, string FolderName, string ProcessName);

/// <summary>
/// Where CapCut keeps its builds, its drafts and its caches, and which of them may go.
///
/// <para><b>Researched, not observed</b> (see <see cref="ProviderBasis"/>). Nothing here was seen on a
/// machine of Deguffer's own. The names come from a real CapCut 3.8 tree published by mistake in a
/// public repository, from two open-source tools that pin CapCut's version by editing its launcher
/// record, from tools that read Jianying Pro's drafts, and from one user's measured clean of Jianying
/// Pro's cache that a published cleaner adopted. CapCut's own help names no folder.</para>
///
/// <para><b>The tree:</b></para>
/// <list type="bullet">
/// <item><c>Apps\</c>: one folder per build, named by its four-part version (<c>3.8.0.1431</c>); the
/// launcher stub <c>CapCut.exe</c>; <c>uninst.exe</c>; and <c>configure.ini</c>, whose
/// <c>last_version</c> names the build the launcher starts. See
/// <see cref="CapCutSupersededBuildProvider"/>.</item>
/// <item><c>User Data\Projects\com.lveditor.draft\</c>: the user's drafts. Never touched.</item>
/// <item><c>User Data\Config\</c>: settings, shortcuts and automatic draft backups. Never touched.</item>
/// <item><c>User Data\Cache\</c>: the caches, of two kinds. See <see cref="CacheChildren"/>.</item>
/// </list>
///
/// <para><b>Only part of <c>Cache</c> is a cache.</b> The one field test (Jianying Pro 11.1, every
/// folder measured, removed and the drafts reopened) found two kinds of folder in it. Some hold what
/// the editor derived from the user's media and makes again: thumbnails, waveforms, the audio it
/// extracted to write subtitles, algorithm results, renders of a single effect. Others hold what it
/// downloaded or imported for a draft: <c>effect</c> is named in draft files thousands of times, and
/// <c>image</c> and <c>importcache3</c> hold copies of imported media that may be the only copy left.
/// So <c>Cache</c> is a root whose recognised children are the first kind, and everything else in it
/// is Tier 4 (§5.2), named in <see cref="Survivors"/> where it is known.</para>
///
/// <para><b>Rendered media is left alone even where the field test removed it.</b>
/// <c>prerender</c> and <c>RemuxCache</c> regenerated there, but they hold transcoded footage that
/// can outlive the original file, and CapCut's settings move both
/// (<c>prerenderCacheLocation</c>, <c>remuxFileLocation</c>), which would put Deguffer in a folder the
/// user chose.</para>
/// </summary>
public static class CapCutLayout
{
    /// <summary>The international edition.</summary>
    public static readonly CapCutEdition CapCut = new("CapCut", "CapCut", "CapCut");

    /// <summary>The mainland Chinese edition, which keeps the same tree under its own name.</summary>
    public static readonly CapCutEdition JianyingPro = new("Jianying Pro", "JianyingPro", "JianyingPro");

    public static readonly IReadOnlyList<CapCutEdition> Editions = [CapCut, JianyingPro];

    /// <summary>The folder each build is installed in, beside the launcher and its record.</summary>
    public const string AppsFolder = "Apps";

    public const string UserDataFolder = "User Data";

    public const string CacheFolder = "Cache";

    /// <summary>
    /// The children of <c>User Data\Cache</c> the editor derives from the user's media and makes again
    /// the next time a draft needs them.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Reason)> CacheChildren =
    [
        ("frameThumbnail", "Thumbnails of video frames, drawn again when a draft's timeline is shown."),
        ("audioWave", "Waveforms drawn from audio clips, drawn again when a draft is opened."),
        ("recognize", "Audio extracted from clips to write automatic subtitles. Subtitles already "
            + "written are in the draft, and the audio is extracted again only if subtitles are made again."),
        ("AlgorithmCache", "Results of the editor's analysis of clips, worked out again when needed."),
        ("MotionBlurCache", "Frames rendered for the motion blur effect, rendered again when needed."),
        ("ttsTemp", "Speech generated from text, kept while it was being placed in a draft."),
        ("tmp", "The editor's scratch space."),
    ];

    /// <summary>
    /// The editor's own folder, where §5.6 asserts what survived. Relative to the edition's folder under
    /// <c>%LOCALAPPDATA%</c>. The first entries are the user's work and settings; the rest are the
    /// children of <c>Cache</c> that are not caches.
    /// </summary>
    public static readonly IReadOnlyList<(string RelativePath, string Reason)> Survivors =
    [
        (Path.Combine(UserDataFolder, "Projects"), "Your drafts. Deguffer never removes them."),
        (Path.Combine(UserDataFolder, "Config"), "The editor's settings, shortcuts and automatic draft backups."),
        (AppsFolder, "The installed builds, the launcher and its record of which build to start."),
        (CacheChild("effect"), "Effects downloaded for your drafts, which the drafts name directly."),
        (CacheChild("onlineMaterial"), "Stickers, music and other material downloaded for your drafts."),
        (CacheChild("artistEffect"), "Creator effects downloaded for your drafts."),
        (CacheChild("music"), "Music downloaded for your drafts."),
        (CacheChild("template"), "Templates downloaded for your drafts."),
        (CacheChild("image"), "Copies of images imported into drafts, which may be the only copy left."),
        (CacheChild("importcache3"), "Copies of media imported into drafts, which may be the only copy left."),
        (CacheChild("agencycache"), "Media the editor transcoded for your drafts, which may outlive the original."),
        (CacheChild("prerender"), "Renders of your drafts' footage, which may outlive the original."),
        (CacheChild("RemuxCache"), "Footage the editor re-packaged, which may outlive the original."),
    ];

    /// <summary>The edition's own folder under <c>%LOCALAPPDATA%</c>.</summary>
    public static string FolderIn(IUserEnvironment environment, CapCutEdition edition) =>
        Path.Combine(environment.LocalAppData, edition.FolderName);

    /// <summary>The edition's <c>Apps</c> folder.</summary>
    public static string AppsIn(IUserEnvironment environment, CapCutEdition edition) =>
        Path.Combine(FolderIn(environment, edition), AppsFolder);

    /// <summary>The edition's <c>User Data\Cache</c> folder.</summary>
    public static string CacheIn(IUserEnvironment environment, CapCutEdition edition) =>
        Path.Combine(FolderIn(environment, edition), UserDataFolder, CacheFolder);

    /// <summary>
    /// The edition's folder as a <see cref="DeclaredRoot"/> whose locations are the cache children that
    /// regenerate. The folder, <c>User Data</c> and <c>Cache</c> are never targets.
    /// </summary>
    public static DeclaredRoot Declare(IUserEnvironment environment, CapCutEdition edition) => new(
        FolderIn(environment, edition),
        $"{edition.Name}'s own folder must survive. Your drafts, its settings and its installed builds are "
        + "in it, and only caches it makes again are removed.",
        RequiresElevation: false,
        [.. CacheChildren.Select(child => new DeclaredLocation(CacheChild(child.Name), child.Reason))],
        Survivors);

    /// <summary>
    /// §5.2 as §7.1 reads it. The edition's folder and <c>User Data</c> recognise nothing, and
    /// <c>Cache</c> recognises only the children that regenerate. <c>Apps</c> is
    /// <see cref="CapCutSupersededBuildProvider"/>'s to declare.
    /// </summary>
    public static IReadOnlyList<ToolRoot> CacheRoots(IUserEnvironment environment, CapCutEdition edition) =>
    [
        new ToolRoot(
            FolderIn(environment, edition),
            $"This is {edition.Name}'s own folder, with your drafts, its settings and its installed builds. "
            + "Deguffer removes only caches inside it that the editor makes again.",
            static _ => false),
        new ToolRoot(
            Path.Combine(FolderIn(environment, edition), UserDataFolder),
            $"This is where {edition.Name} keeps your drafts and its settings. Deguffer removes only caches "
            + "inside it that the editor makes again.",
            static _ => false),
        ToolRoot.Of(
            CacheIn(environment, edition),
            $"This is {edition.Name}'s cache folder. Some of it is material your drafts use, which Deguffer "
            + "leaves alone. It removes only the folders the editor makes again.",
            new DisposableChildSet(
            [
                .. CacheChildren.Select(child =>
                    new ChildClassification(child.Name, SafetyTier.RegenerableCache, child.Reason)),
            ])),
    ];

    private static string CacheChild(string name) => Path.Combine(UserDataFolder, CacheFolder, name);
}
