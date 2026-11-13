namespace Deguffer.Core.Providers;

/// <summary>
/// The Hugging Face hub cache as one planning pass read it, before and as the tool was asked about it.
/// </summary>
/// <param name="Hub">The hub cache the tool reported and was asked to list.</param>
/// <param name="Entries">Everything in the hub cache, listed before the tool was asked, which the §5.6 negative is read from.</param>
/// <param name="Repositories">What <c>hf cache ls</c> listed.</param>
internal sealed record HuggingFaceHubListing(
    string Hub,
    IReadOnlyList<FileSystemInfo> Entries,
    IReadOnlyList<HuggingFaceRepository> Repositories);
