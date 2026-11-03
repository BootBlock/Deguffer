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
