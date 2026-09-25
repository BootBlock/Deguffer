using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests;

/// <summary>
/// The real check against a mutex this test owns, because it decides whether a Roslyn session is
/// deleted: "gone" must mean gone, and nothing else.
/// </summary>
public sealed class NamedMutexesTests
{
    [Fact]
    public void AnswersWhetherAMutexOfThatNameExistsInThisSession()
    {
        var name = Guid.NewGuid().ToString("N");

        Assert.False(NamedMutexes.Default.Exists(name));

        using (new Mutex(initiallyOwned: false, name))
        {
            Assert.True(NamedMutexes.Default.Exists(name));
        }

        Assert.False(NamedMutexes.Default.Exists(name));
    }
}
