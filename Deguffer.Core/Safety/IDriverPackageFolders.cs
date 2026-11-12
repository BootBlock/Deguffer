namespace Deguffer.Core.Safety;

/// <summary>
/// Where each driver package is in the driver store. Behind an interface because the real one asks
/// about the driver store of whoever runs the suite.
/// </summary>
public interface IDriverPackageFolders
{
    /// <summary>
    /// The folder of the package Windows now calls <paramref name="publishedName"/>, or null where no
    /// package has that name.
    /// </summary>
    string? FolderOf(string publishedName);
}
