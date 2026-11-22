using Deguffer.Core.Providers;

namespace Deguffer.Testing;

/// <summary>
/// Asks a <see cref="RowDeclarations"/> about one folder through the question a plan asks, so a test
/// of the declarations proves what every row is answered.
/// </summary>
public static class RowDeclarationQuestions
{
    public static async Task<bool> OfferedByARowAsync(
        this RowDeclarations declarations, string folder, CancellationToken ct = default) =>
        (await declarations.UnofferedAsync([(folder, "A folder a test asks about.")], ct).ConfigureAwait(false)).Count == 0;
}
