namespace Deguffer.Core.Exploring.Files;

/// <summary>
/// What kind of file a name says it is, by its extension and nothing else.
///
/// <para>A description, never a classification (§7.1). No category is safe, junk or a cache, and the
/// order here is the order a reader scans the list in rather than any ranking: "installers" says
/// what the files are, not that they can go.</para>
///
/// <para>The values are ordinal and a picker lists them in this order. See
/// <see cref="FileCategories"/> for which extension falls where.</para>
/// </summary>
public enum FileCategory
{
    Video = 0,
    Audio = 1,
    Images = 2,
    Documents = 3,
    Archives = 4,
    DiskImages = 5,
    VirtualMachineDisks = 6,
    Installers = 7,
    CodeAndBuildOutput = 8,
    Other = 9,
}
