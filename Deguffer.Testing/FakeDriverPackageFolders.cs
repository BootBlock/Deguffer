using Deguffer.Core.Safety;

namespace Deguffer.Testing;

/// <summary>
/// Driver package folders a test states. The real lookup asks SetupAPI about the driver store of
/// whoever runs the suite.
/// </summary>
/// <param name="root">The store every package is placed in, as <c>&lt;root&gt;\&lt;published name&gt;</c>.</param>
public sealed class FakeDriverPackageFolders(string root) : IDriverPackageFolders
{
    public string? FolderOf(string publishedName) => Path.Combine(root, publishedName);
}
