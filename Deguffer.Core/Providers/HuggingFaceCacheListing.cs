using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Deguffer.Core.Providers;

/// <summary>
/// One repository the Hugging Face hub cache holds, as <c>hf cache ls</c> lists it.
/// </summary>
/// <param name="Id">
/// What <c>hf cache rm</c> is told: the type, a slash, then the repository, such as
/// <c>model/openai-community/gpt2</c>. The type is what keeps a model and a dataset of one name apart.
/// </param>
/// <param name="RepoId">The repository as the Hub names it, such as <c>openai-community/gpt2</c>.</param>
/// <param name="Type">The kind of repository.</param>
/// <param name="Bytes">
/// What the tool says the repository's files take up, read back from the figure it prints to three
/// figures, so it is close rather than exact.
/// </param>
internal sealed record HuggingFaceRepository(string Id, string RepoId, HuggingFaceRepositoryType Type, long Bytes)
{
    /// <summary>
    /// The folder <c>huggingface_hub</c> keeps the repository in, inside the hub cache:
    /// <c>repo_folder_name</c> in <c>file_download.py</c>, the plural type and the parts of the
    /// repository joined with <c>--</c>. Only one repository can have it, because a repository name
    /// may not hold <c>--</c>, which <see cref="HuggingFaceCacheListing"/> checks.
    /// </summary>
    public string Folder => $"{Plural}--{RepoId.Replace("/", "--", StringComparison.Ordinal)}";

    private string Plural => Type switch
    {
        HuggingFaceRepositoryType.Model => "models",
        HuggingFaceRepositoryType.Dataset => "datasets",
        HuggingFaceRepositoryType.Space => "spaces",
        HuggingFaceRepositoryType.Kernel => "kernels",
        _ => throw new InvalidOperationException($"No folder prefix for {Type}."),
    };
}

/// <summary>
/// Reads what <c>hf cache ls --format json</c> printed.
///
/// <para><b>Each repository has to be one Deguffer can name exactly.</b> The command is told a
/// repository by its id and the folder is found by the tool's own naming rule, so the two have to be
/// the same repository. An id whose type, separators or characters the Hub would not give a
/// repository fails the whole listing rather than being skipped: the command matches ids without
/// regard to case, and a listing this cannot read in full is not one to choose from.</para>
///
/// <para><b>The size is the tool's figure, printed for people.</b> <c>_format_size</c> divides by
/// 1,000 for each unit and keeps one decimal place, so <c>3.1G</c> reads back as 3,100,000,000 bytes.
/// That is the only size the command gives, and with the shared blob store it is also the only one
/// that counts a repository's files: their folder then holds links, and the files are in
/// <c>hub\blobs</c>.</para>
/// </summary>
internal static partial class HuggingFaceCacheListing
{
    /// <summary>The kinds of repository <c>REPO_TYPES_MAPPING</c> in <c>constants.py</c> keeps a folder for.</summary>
    private static readonly FrozenDictionary<string, HuggingFaceRepositoryType> Types = new Dictionary<string, HuggingFaceRepositoryType>
    {
        ["model"] = HuggingFaceRepositoryType.Model,
        ["dataset"] = HuggingFaceRepositoryType.Dataset,
        ["space"] = HuggingFaceRepositoryType.Space,
        ["kernel"] = HuggingFaceRepositoryType.Kernel,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The units <c>_format_size</c> prints, each a thousand times the one before.</summary>
    private const string Units = "KMGTPEZY";

    /// <summary>
    /// The repositories listed, which is empty where the cache holds none, or null where the listing
    /// could not be read.
    /// </summary>
    public static IReadOnlyList<HuggingFaceRepository>? TryRead(string standardOutput)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(standardOutput);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                return null;
            }

            var repositories = new List<HuggingFaceRepository>(document.RootElement.GetArrayLength());

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (TryReadEntry(entry) is not { } repository)
                {
                    return null;
                }

                repositories.Add(repository);
            }

            // The command finds a repository by its id without regard to case, so two ids that differ
            // only in case name one repository to it and two to anything reading this list.
            return repositories.DistinctBy(repository => repository.Id, StringComparer.OrdinalIgnoreCase).Count() == repositories.Count
                ? repositories
                : null;
        }
    }

    /// <summary>
    /// A size as <c>_format_size</c> prints it, in bytes, or null where it is not one: digits, an
    /// optional decimal part, and an optional unit.
    /// </summary>
    public static long? TryReadSize(string printed)
    {
        if (PrintedSize().Match(printed) is not { Success: true } match
            || !decimal.TryParse(match.Groups["number"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        var unit = match.Groups["unit"].Value;
        var scale = unit.Length == 0 ? 0 : Units.IndexOf(unit[0], StringComparison.Ordinal) + 1;

        // A figure past what a long holds is not a cache on a disk.
        try
        {
            return (long)(number * (decimal)Math.Pow(1000, scale));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static HuggingFaceRepository? TryReadEntry(JsonElement entry)
    {
        if (entry.ValueKind is not JsonValueKind.Object
            || Text(entry, "id") is not { } id
            || Text(entry, "repo_id") is not { } repoId
            || Text(entry, "repo_type") is not { } type
            || Text(entry, "size") is not { } size
            || TryReadSize(size) is not { } bytes)
        {
            return null;
        }

        // validate_repo_id refuses "--" and "..", and the first is what makes the folder name one repository's.
        return Types.TryGetValue(type, out var kind)
            && id == $"{type}/{repoId}"
            && RepositoryName().IsMatch(repoId)
            && !repoId.Contains("--", StringComparison.Ordinal)
            && !repoId.Contains("..", StringComparison.Ordinal)
                ? new HuggingFaceRepository(id, repoId, kind, bytes)
                : null;
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// A repository name as <c>validate_repo_id</c> allows one: an optional namespace and a name, each
    /// of letters, digits, <c>-</c>, <c>_</c> and <c>.</c>, and not starting or ending with <c>-</c> or
    /// <c>.</c>. Narrower than Python's <c>\w</c>, which admits letters from every script: a name
    /// outside this is one Deguffer leaves to the user rather than guesses a folder for.
    /// </summary>
    [GeneratedRegex(@"\A(?:[A-Za-z0-9_](?:[A-Za-z0-9_.\-]*[A-Za-z0-9_])?/)?[A-Za-z0-9_](?:[A-Za-z0-9_.\-]*[A-Za-z0-9_])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryName();

    [GeneratedRegex(@"\A(?<number>[0-9]{1,15}(?:\.[0-9]{1,6})?)(?<unit>[KMGTPEZY]?)\z", RegexOptions.CultureInvariant)]
    private static partial Regex PrintedSize();
}
