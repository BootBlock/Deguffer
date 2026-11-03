using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// One Topaz Labs product's folder under <c>%PROGRAMDATA%\Topaz Labs LLC</c>, and what may go from it.
/// </summary>
/// <param name="Name">The folder's name, which is the product's: <c>Topaz Photo AI</c>.</param>
/// <param name="Folder">The folder, in display form.</param>
/// <param name="Standing">Whether the product is still installed.</param>
/// <param name="Unreadable">The folder would not be listed, so nothing in it was examined.</param>
/// <param name="Kept">
/// Every entry standing beside what may go, named so §5.6 asserts each survived: the product's logs
/// beside <c>models</c>, and the sign-in record inside it where only the inner folder goes.
/// </param>
/// <param name="Target">The models folder that may go, or null.</param>
/// <param name="Declined">A models folder found and left alone, with why, or null.</param>
public sealed record TopazProduct(
    string Name,
    string Folder,
    TopazInstallStanding Standing,
    bool Unreadable,
    IReadOnlyList<(string Path, string Reason)> Kept,
    string? Target,
    (string Path, string Reason)? Declined);

/// <summary>How far Deguffer got into <c>%PROGRAMDATA%\Topaz Labs LLC</c>.</summary>
public enum TopazRootReach
{
    Absent,

    /// <summary>Windows would not describe it.</summary>
    Refused,

    /// <summary>It is a link, which is never looked through.</summary>
    Link,

    /// <summary>It is there and would not be listed.</summary>
    Unlisted,

    Listed,
}

/// <summary>Everything one look at Topaz's machine-wide folder found.</summary>
/// <param name="Links">Children of the vendor folder that are links, never followed.</param>
/// <param name="Strangers">Children of the vendor folder that are not named as a product's.</param>
public sealed record TopazSurvey(
    string Root,
    TopazRootReach Reach,
    IReadOnlyList<string> Links,
    IReadOnlyList<string> Strangers,
    IReadOnlyList<TopazProduct> Products);

/// <summary>
/// Where Topaz Labs keeps the machine-learning models its applications download, and the rules that
/// say which folder in there may go.
///
/// <para><b>The layout.</b> Each product has a folder in <c>%PROGRAMDATA%\Topaz Labs LLC</c> named for
/// the product, and its models are in <c>models</c> inside it. Topaz Video puts the model data one
/// level further down, in <c>models\models</c>, which Topaz's own troubleshooting page names. The
/// vendor folder holds nothing but product folders, so a child whose name does not start with
/// <c>Topaz </c> is not recognised.</para>
///
/// <para><b>What is beside the models decides how much may go.</b> Topaz Video AI was reported keeping
/// <c>auth.tz</c>, the product's sign-in record, and <c>tvai.tz</c> at the top of <c>models</c>, beside
/// the model files. For a product that is no longer installed nothing reads either, and the whole
/// <c>models</c> folder goes. For one that may still be installed, a <c>models</c> folder holding one of
/// them is never removed whole: only an inner <c>models</c> folder holding none of them may go, which is
/// Topaz Video's layout, and otherwise the folder is named and left alone. A single model file is never
/// a target, because Topaz support reports a part-removed set failing with "Error Loading Model" rather
/// than downloading again.</para>
/// </summary>
public static class TopazLayout
{
    /// <summary>The vendor's folder name, in <c>%PROGRAMDATA%</c> and in each program directory.</summary>
    public const string VendorFolderName = "Topaz Labs LLC";

    /// <summary>The folder each product keeps its models in, and the inner one Topaz Video adds.</summary>
    public const string ModelsName = "models";

    private const string ProductPrefix = "Topaz ";

    /// <summary>
    /// The files a product keeps at the top of a models folder that a model download may not bring
    /// back, and what each is.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KeptFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth.tz"] = "The product's sign-in record, kept beside its models. Removing it signs the product out.",
            ["auth.tpz"] = "The product's sign-in record, kept beside its models. Removing it signs the product out.",
            ["tvai.tz"] = "A file Topaz Video keeps beside its models that a model download may not bring back.",
        };

    /// <summary>
    /// §5.3: the applications that load these models, current and discontinued, and the hosts Topaz's
    /// plugins run inside. A model loaded while a folder is removed is a part-removed set.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames =
    [
        "Topaz Photo", "Topaz Photo AI", "tpai",
        "Topaz Gigapixel", "Topaz Gigapixel AI", "gigapixel", "Topaz A.I. Gigapixel",
        "Topaz Video", "Topaz Video AI", "Topaz Video Enhance AI",
        "Topaz DeNoise AI", "Topaz Sharpen AI", "Topaz Mask AI", "Topaz Adjust AI",
        "Photoshop", "Lightroom",
    ];

    private const string BesideReason =
        "Beside the product's models. Topaz keeps its logs and its own files here, and only the models go.";

    private const string FileWhereFolderReason = "A file where Topaz keeps a folder, so Deguffer left it alone.";

    /// <summary>The vendor folder, ordinarily <c>C:\ProgramData\Topaz Labs LLC</c>.</summary>
    public static string Root(ISystemDirectories system) => Path.Combine(system.ProgramData, VendorFolderName);

    /// <summary>Whether a child of the vendor folder is named as a product's folder.</summary>
    public static bool IsProductName(string name) =>
        name.Length > ProductPrefix.Length && name.StartsWith(ProductPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Look at the vendor folder and every product folder in it, never through a link.</summary>
    public static TopazSurvey Survey(string root, TopazInstallations installations, CancellationToken ct)
    {
        switch (LongPath.ProbeDirectory(root))
        {
            case PathPresence.Absent:
                return new TopazSurvey(root, TopazRootReach.Absent, [], [], []);

            case PathPresence.Refused:
                return new TopazSurvey(root, TopazRootReach.Refused, [], [], []);
        }

        if (LongPath.IsReparsePoint(root))
        {
            return new TopazSurvey(root, TopazRootReach.Link, [], [], []);
        }

        var scan = ChildDirectories.Under(root);

        if (scan.Unreadable)
        {
            return new TopazSurvey(root, TopazRootReach.Unlisted, [], [], []);
        }

        List<string> strangers = [];
        List<TopazProduct> products = [];

        foreach (var child in scan.Directories)
        {
            ct.ThrowIfCancellationRequested();

            var folder = LongPath.Display(child.FullName);

            if (IsProductName(child.Name))
            {
                products.Add(Examine(child.Name, folder, installations.Of(folder)));
            }
            else
            {
                strangers.Add(folder);
            }
        }

        return new TopazSurvey(
            root,
            TopazRootReach.Listed,
            [.. scan.Links.Select(link => LongPath.Display(link.FullName))],
            strangers,
            products);
    }

    /// <summary>
    /// One product folder. The whole folder is listed, and a folder that will not be listed is left
    /// alone, so that everything standing beside the models has been seen and can be asserted.
    /// </summary>
    private static TopazProduct Examine(string name, string folder, TopazInstallStanding standing)
    {
        var product = new TopazProduct(name, folder, standing, Unreadable: false, [], Target: null, Declined: null);

        if (FolderEntries.Of(folder) is not { } entries)
        {
            return product with { Unreadable = true };
        }

        List<(string Path, string Reason)> kept = [];
        string? models = null;
        (string Path, string Reason)? declined = null;

        foreach (var entry in entries)
        {
            var path = LongPath.Display(entry.FullName);

            if (!entry.Name.Equals(ModelsName, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add((path, BesideReason));
            }
            else if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                declined = (path, CacheLevelWalk.LinkReason);
            }
            else if (entry is DirectoryInfo)
            {
                models = path;
            }
            else
            {
                kept.Add((path, FileWhereFolderReason));
            }
        }

        if (models is null)
        {
            return product with { Kept = kept, Declined = declined };
        }

        // Nothing reads the sign-in record of a product that is gone, so the whole folder goes.
        if (standing is TopazInstallStanding.NotInstalled)
        {
            return product with { Kept = kept, Target = models };
        }

        var (target, inner, refusal) = WhatMayGo(models);
        kept.AddRange(inner);

        return product with { Kept = kept, Target = target, Declined = refusal };
    }

    /// <summary>
    /// For a product that may be installed: the models folder if nothing at its top is a file the
    /// product keeps, otherwise the inner models folder on the same test, otherwise nothing.
    /// </summary>
    private static (string? Target, List<(string Path, string Reason)> Kept, (string Path, string Reason)? Declined) WhatMayGo(string models)
    {
        if (FolderEntries.Of(models) is not { } entries)
        {
            return (null, [], (models, UnlistedReason));
        }

        if (!entries.Any(IsKeptFile))
        {
            return (models, [], null);
        }

        List<(string Path, string Reason)> kept = [(models, "The product's models folder itself, which holds its sign-in record.")];
        string? inner = null;

        foreach (var entry in entries)
        {
            var path = LongPath.Display(entry.FullName);

            if (entry is DirectoryInfo
                && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                && entry.Name.Equals(ModelsName, StringComparison.OrdinalIgnoreCase))
            {
                inner = path;
            }
            else
            {
                kept.Add((path, KeptFiles.TryGetValue(entry.Name, out var why) ? why : BesideReason));
            }
        }

        if (inner is null)
        {
            return (null, kept, (models, SignInReason));
        }

        if (FolderEntries.Of(inner) is not { } innerEntries)
        {
            return (null, kept, (inner, UnlistedReason));
        }

        return innerEntries.Any(IsKeptFile)
            ? (null, kept, (inner, SignInReason))
            : (inner, kept, null);
    }

    /// <summary>Why an installed product's models folder holding its sign-in record is left alone.</summary>
    public const string SignInReason =
        "It holds the product's sign-in record beside the models, and the product may still be installed, "
        + "so removing the folder would sign it out. Use the product's own model controls instead.";

    private const string UnlistedReason =
        "Deguffer could not list it, so whether it holds the product's sign-in record is not known.";

    private static bool IsKeptFile(FileSystemInfo entry) => entry is FileInfo && KeptFiles.ContainsKey(entry.Name);
}
