using System.Collections.Frozen;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Providers;

/// <summary>
/// A store Git LFS may prune, the remote it checks against, and the arguments for the dry run and the
/// clean. Both name the remote, so a <c>lfs.pruneremotetocheck</c> set after the preview cannot send
/// them to one this did not check.
/// </summary>
internal sealed record GitLfsPrune(GitLfsCheckout Checkout, string Remote)
{
    /// <summary>
    /// The preview: prune's dry run, listing every object it would remove. Forced progress is what
    /// makes it list them all (<see cref="GitLfsPruneReport"/>). A dry run never asks the remote, and
    /// removes nothing from the store.
    /// </summary>
    public string DryRunArguments => Checkout.Arguments($"{RemoteArgument} -c lfs.forceprogress=true lfs prune --dry-run --verbose");

    /// <summary>
    /// The clean. <c>--verify-remote</c> asks the remote's server for every object before any is
    /// removed, and prune removes none if one is missing there or the server does not answer. Signing
    /// in is never asked for: Git Credential Manager would open a window in the middle of a clean, so it
    /// is told not to, and a server that needs it leaves the store as it was.
    /// </summary>
    public string PruneArguments => Checkout.Arguments($"{RemoteArgument} -c credential.interactive=never lfs prune --verify-remote");

    private string RemoteArgument => $"-c {GitLfsCheckout.Quoted("lfs.pruneremotetocheck=" + Remote)}";
}

/// <summary>A store's prune, or why it is not offered.</summary>
/// <param name="WhyNot">Why <paramref name="Prune"/> is null, as a sentence for the plan.</param>
/// <param name="Unanswered">Whether Git LFS could not answer, rather than answered with a reason not to prune.</param>
internal sealed record GitLfsPruneReading(GitLfsPrune? Prune, string WhyNot, bool Unanswered);

/// <summary>
/// Whether Git LFS can check a store's remote before it removes anything, read from
/// <c>git lfs env</c>.
///
/// <para><b>The Tier 2 claim rests on the check, and Git LFS skips it for a folder.</b> An object
/// prune removes is a download again only if the remote still has it. Prune decides what was pushed
/// from the local remote-tracking refs, and an object only the reflog names, as a commit amended
/// before it was pushed leaves, is not kept. <c>--verify-remote</c> is what stops prune removing
/// such an object, and for an endpoint that is a <c>file</c> URL Git LFS trusts its standalone
/// transfer agent instead of asking (<c>tq/transfer_queue.go</c>, git-lfs 3.8.1, observed against a
/// folder remote with an object deleted from it). So a store is offered only where the remote prune
/// checks has an endpoint that is a server, and where that remote is one the repository has: with no
/// such remote nothing counts as pushed, and nothing is there to check against.</para>
///
/// <para><b>The endpoint is read for the remote prune checks.</b> <c>git lfs env</c> prints
/// <c>Endpoint=</c> for the default remote and <c>Endpoint (&lt;name&gt;)=</c> for each other one,
/// each followed by <c>(auth=…)</c>, and <c>PruneRemoteName=</c> for the one prune checks. Observed
/// against git-lfs 3.8.1, with <c>lfs.url</c> and <c>lfs.pruneremotetocheck</c> both set and unset.</para>
/// </summary>
internal static class GitLfsRemote
{
    private const string PruneRemoteLine = "PruneRemoteName=";

    private const string DefaultEndpointLine = "Endpoint=";

    private const string AuthSuffix = " (auth=";

    /// <summary>
    /// The schemes of a server. git-lfs reaches an <c>ssh</c> remote through its batch API as it reaches
    /// one over HTTPS, and asks it the same question.
    /// </summary>
    private static readonly FrozenSet<string> ServerSchemes =
        FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "https", "http", "ssh");

    public static async Task<GitLfsPruneReading> ReadAsync(
        IProcessRunner runner,
        string git,
        GitLfsCheckout checkout,
        CancellationToken ct)
    {
        var repository = GitLfsPlanNotes.Describe(checkout);
        var outcome = await runner.RunAsync(git, checkout.EnvironmentArguments, ToolRun.Query, ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return new(null, $"Git LFS could not describe {repository}, so nothing is offered for it. It said: {outcome.Message}", Unanswered: true);
        }

        var lines = outcome.StandardOutput.Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        if (lines.FirstOrDefault(line => line.StartsWith(PruneRemoteLine, StringComparison.Ordinal)) is not { } named
            || named[PruneRemoteLine.Length..] is not { Length: > 0 } remote)
        {
            return new(null, $"Git LFS did not say which remote it checks for {repository}, so nothing is offered for it.", Unanswered: true);
        }

        if (!checkout.Remotes.Contains(remote, StringComparer.Ordinal))
        {
            return Declined(
                $"{repository} has no remote called '{remote}', which is the one Git LFS would ask whether it still "
                + "has each file, so removing one could lose the only copy. Nothing is offered for it.");
        }

        // A remote other than the default is listed by name, and the default is the one listed without.
        var endpoint = Endpoint(lines, $"Endpoint ({remote})=") ?? Endpoint(lines, DefaultEndpointLine);

        if (endpoint is null || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !ServerSchemes.Contains(uri.Scheme))
        {
            return Declined(
                $"The remote Git LFS checks for {repository} is '{endpoint}', which is not a server it can ask whether it "
                + "still has a file before removing it, so removing one could lose the only copy. Nothing is offered for it.");
        }

        return new(new GitLfsPrune(checkout, remote), string.Empty, Unanswered: false);
    }

    /// <summary>The URL on the line opening with <paramref name="opening"/>, without the authentication note after it.</summary>
    private static string? Endpoint(IReadOnlyList<string> lines, string opening)
    {
        if (lines.FirstOrDefault(line => line.StartsWith(opening, StringComparison.Ordinal)) is not { } line)
        {
            return null;
        }

        var value = line[opening.Length..];
        var auth = value.LastIndexOf(AuthSuffix, StringComparison.Ordinal);

        return auth < 0 ? value : value[..auth];
    }

    private static GitLfsPruneReading Declined(string why) => new(null, why, Unanswered: false);
}
