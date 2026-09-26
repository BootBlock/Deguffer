using Deguffer.Core.Safety;

namespace Deguffer.Core.Tests.Fakes;

/// <summary>
/// A driver store whose listing a test states, over folders the test built. The real one lists the
/// driver store of whoever runs the suite.
/// </summary>
public sealed class FakeDriverStore(Func<DriverStoreListing> listing) : IDriverStore
{
    private readonly Dictionary<string, string?> _names = new(StringComparer.OrdinalIgnoreCase);

    public FakeDriverStore(params DriverPackage[] packages)
        : this(() => new DriverStoreListing(packages))
    {
        foreach (var package in packages)
        {
            _names[package.PublishedName] = package.Folder;
        }
    }

    /// <summary>How many times the store was listed, so a test can hold the provider to once a pass.</summary>
    public int Listings { get; private set; }

    public Task<DriverStoreListing> ListAsync(CancellationToken ct)
    {
        Listings++;
        return Task.FromResult(listing());
    }

    public string? FolderOf(string publishedName) => _names.GetValueOrDefault(publishedName);

    /// <summary>
    /// Windows giving <paramref name="publishedName"/> to the package in <paramref name="folder"/>, or to
    /// nothing, as it does when a package is removed and another is staged after the preview.
    /// </summary>
    public void Rename(string publishedName, string? folder) => _names[publishedName] = folder;
}
