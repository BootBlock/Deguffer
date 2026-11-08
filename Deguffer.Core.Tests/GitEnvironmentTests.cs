using Deguffer.Core.Providers;
using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// The variables every git Deguffer starts is started without, against the git on this machine.
///
/// <para>Git is required rather than skipped when absent, because a test that passes when git could
/// not be found has proved nothing.</para>
/// </summary>
public sealed class GitEnvironmentTests
{
    private static readonly string Git = UserEnvironment.Current.FindExecutable("git")
        ?? throw new InvalidOperationException("These tests run a real git, and none was found on PATH.");

    /// <summary>
    /// The list is the one git clears before it runs a command in another repository. A later git that
    /// adds a variable to it fails this test, rather than leaving Deguffer's git open to that variable.
    /// </summary>
    [Fact]
    public async Task UnsetsEveryVariableGitTreatsAsNamingARepository()
    {
        var outcome = await ProcessRunner.Default
            .RunAsync(Git, "rev-parse --local-env-vars", GitEnvironment.Query, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Message);

        var listed = outcome.StandardOutput.Split(
            ["\r\n", "\n"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(listed.Order(StringComparer.Ordinal), GitEnvironment.RepositoryVariables.Order(StringComparer.Ordinal));
    }
}
