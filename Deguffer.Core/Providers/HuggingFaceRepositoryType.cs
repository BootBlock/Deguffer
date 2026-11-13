namespace Deguffer.Core.Providers;

/// <summary>
/// The kinds of repository the Hugging Face hub cache keeps a folder for: <c>REPO_TYPES_MAPPING</c>
/// in <c>constants.py</c>.
/// </summary>
internal enum HuggingFaceRepositoryType
{
    Model,
    Dataset,
    Space,
    Kernel,
}

internal static class HuggingFaceRepositoryTypeExtensions
{
    /// <summary>The heading a repository is listed under, by what kind of repository it is.</summary>
    public static string Heading(this HuggingFaceRepositoryType type) => type switch
    {
        HuggingFaceRepositoryType.Model => "Models",
        HuggingFaceRepositoryType.Dataset => "Datasets",
        HuggingFaceRepositoryType.Space => "Spaces",
        HuggingFaceRepositoryType.Kernel => "Kernels",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
