using Deguffer.Core.Configuration;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// Settings stated by the test rather than read from a file, so a provider whose route the user
/// chooses can be driven down either branch with no settings file anywhere.
/// </summary>
public sealed class FakePreferences(AppPreferences current) : ICurrentPreferences
{
    public AppPreferences Current { get; set; } = current;
}
