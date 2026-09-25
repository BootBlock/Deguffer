using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Reading Capture One's own record of the catalogs and sessions it opened, which is the only place
/// that says where they are.
///
/// <para>The fixtures are invented, in the shape .NET writes a <c>user.config</c>. Capture One was
/// not installed where this was written, so no fixture is a copy of a real file, and the reader is
/// built to depend on nothing but the extension a document path must end in.</para>
/// </summary>
public sealed class CaptureOneDocumentsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string LocalAppData => Path.Combine(_temp.Path, "Local");

    private string WriteSettings(string company, string program, string version, string content)
    {
        var folder = Path.Combine(LocalAppData, company, program, version);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, CaptureOneDocuments.SettingsFileName);
        File.WriteAllText(file, content);
        return file;
    }

    /// <summary>A settings file holding <paramref name="documents"/> as a list of strings.</summary>
    private static string Settings(params string[] documents) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
            <userSettings>
                <CaptureOne.Properties.Settings>
                    <setting name="WindowPlacement" serializeAs="String">
                        <value>0,0,1920,1080</value>
                    </setting>
                    <setting name="RecentlyUsedDocuments" serializeAs="Xml">
                        <value>
                            <ArrayOfString xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                                {string.Concat(documents.Select(d => $"<string>{d}</string>"))}
                            </ArrayOfString>
                        </value>
                    </setting>
                </CaptureOne.Properties.Settings>
            </userSettings>
        </configuration>
        """;

    [Fact]
    public void NoSettingsIsNotFound()
    {
        var documents = CaptureOneDocuments.Read(LocalAppData);

        Assert.False(documents.Found);
        Assert.True(documents.IsComplete);
        Assert.Empty(documents.Catalogs);
        Assert.Empty(documents.Sessions);
    }

    /// <summary>
    /// Both of the places Capture One has kept its settings are read, and a catalog named by its
    /// package, by its database inside the package, or in two settings files is one catalog.
    /// </summary>
    [Fact]
    public void ReadsCatalogsAndSessionsFromEveryVersionsSettings()
    {
        WriteSettings("Capture_One", "CaptureOne.exe_StrongName_abc", "16.4.0.0", Settings(
            @"E:\Shoots\Weddings.cocatalog",
            @"E:\Shoots\2024-06-01\2024-06-01.cosessiondb"));
        WriteSettings("Phase_One", "CaptureOne.exe_StrongName_def", "12.1.0.0", Settings(
            @"E:\Shoots\Weddings.cocatalog\Weddings.cocatalogdb",
            @"C:\Users\testuser\Pictures\Capture One Catalog.cocatalog"));

        var documents = CaptureOneDocuments.Read(LocalAppData);

        Assert.True(documents.Found);
        Assert.True(documents.IsComplete);
        Assert.Equal(2, documents.SettingsFiles.Count);
        Assert.Equal(
            [@"E:\Shoots\Weddings.cocatalog", @"C:\Users\testuser\Pictures\Capture One Catalog.cocatalog"],
            documents.Catalogs);
        Assert.Equal([@"E:\Shoots\2024-06-01"], documents.Sessions);
    }

    /// <summary>
    /// The value's shape is not documented, so a path is found wherever it sits: in an attribute,
    /// with forward slashes, escaped as XML escapes it, or several to one value.
    /// </summary>
    [Fact]
    public void FindsAPathWhateverShapeTheValueHas()
    {
        WriteSettings("Capture_One", "CaptureOne.exe_StrongName_abc", "16.4.0.0", """
            <configuration>
                <userSettings>
                    <Settings>
                        <setting name="RecentlyUsedDocuments" value="D:/Archive/Old.cocatalog" />
                        <setting name="Other">
                            <value>F:\Tom &amp; Jo.cocatalog|F:\Shoot\Shoot.cosessiondb</value>
                        </setting>
                    </Settings>
                </userSettings>
            </configuration>
            """);

        var documents = CaptureOneDocuments.Read(LocalAppData);

        Assert.Equal([@"D:\Archive\Old.cocatalog", @"F:\Tom & Jo.cocatalog"], documents.Catalogs);
        Assert.Equal([@"F:\Shoot"], documents.Sessions);
    }

    /// <summary>
    /// A relative path would resolve against Deguffer's own working directory, and a longer word
    /// that begins with the extension is not the extension.
    /// </summary>
    [Fact]
    public void IgnoresWhatIsNotAFullPathToADocument()
    {
        WriteSettings("Capture_One", "CaptureOne.exe_StrongName_abc", "16.4.0.0", Settings(
            @"Shoots\Relative.cocatalog",
            @"E:\Backups\Weddings.cocatalogs",
            @"E:\Styles\Film.costyle"));

        var documents = CaptureOneDocuments.Read(LocalAppData);

        Assert.True(documents.Found);
        Assert.Empty(documents.Catalogs);
        Assert.Empty(documents.Sessions);
    }

    /// <summary>
    /// A path after another in the same value, or after a URI's scheme, is found whole: no colon can
    /// follow a drive, so the match cannot start at the earlier one.
    /// </summary>
    [Fact]
    public void AnEarlierDriveInTheSameValueDoesNotSwallowThePath()
    {
        WriteSettings("Capture_One", "CaptureOne.exe_StrongName_abc", "16.4.0.0", Settings(
            @"C:\Pictures;E:\Shoots\Weddings.cocatalog",
            "file:///F:/Archive/Old.cocatalog"));

        var documents = CaptureOneDocuments.Read(LocalAppData);

        Assert.Equal([@"E:\Shoots\Weddings.cocatalog", @"F:\Archive\Old.cocatalog"], documents.Catalogs);
    }

    /// <summary>A session file at a drive's root would make the whole drive a session to walk.</summary>
    [Fact]
    public void ASessionFileAtAVolumeRootNamesNoSession()
    {
        WriteSettings("Capture_One", "CaptureOne.exe_StrongName_abc", "16.4.0.0", Settings(@"E:\Stray.cosessiondb"));

        Assert.Empty(CaptureOneDocuments.Read(LocalAppData).Sessions);
    }

    /// <summary>A settings folder that is a link is read through, as Capture One itself reads it.</summary>
    [Fact]
    public void ASettingsFolderThatIsALinkIsRead()
    {
        var real = Path.Combine(_temp.Path, "moved", "16.4.0.0");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, CaptureOneDocuments.SettingsFileName), Settings(@"E:\Shoots\Weddings.cocatalog"));

        var program = Path.Combine(LocalAppData, "Capture_One", "CaptureOne.exe_StrongName_abc");
        Directory.CreateDirectory(program);
        SymbolicLink.ToDirectory(Path.Combine(program, "16.4.0.0"), real);

        Assert.Equal([@"E:\Shoots\Weddings.cocatalog"], CaptureOneDocuments.Read(LocalAppData).Catalogs);
    }

    /// <summary>Another program under the same publisher's folder is none of this reader's business.</summary>
    [Fact]
    public void ReadsOnlyCaptureOnesOwnSettings()
    {
        WriteSettings("Capture_One", "OtherTool.exe_StrongName_abc", "1.0.0.0", Settings(@"E:\Shoots\Weddings.cocatalog"));

        Assert.False(CaptureOneDocuments.Read(LocalAppData).Found);
    }

    /// <summary>
    /// A file that is not well-formed is unread rather than empty, and nothing read before the fault
    /// is used: a partial list cannot claim to be every catalog.
    /// </summary>
    [Fact]
    public void AFileThatIsNotWellFormedIsUnreadAndContributesNothing()
    {
        var file = WriteSettings(
            "Capture_One",
            "CaptureOne.exe_StrongName_abc",
            "16.4.0.0",
            @"<configuration><value>E:\Shoots\Weddings.cocatalog</value><broken>");

        var documents = CaptureOneDocuments.Read(LocalAppData);

        Assert.True(documents.Found);
        Assert.False(documents.IsComplete);
        Assert.Equal([file], documents.Unread);
        Assert.Empty(documents.Catalogs);
    }

    /// <summary>An entity declared in a file another program wrote is never expanded.</summary>
    [Fact]
    public void ADocumentTypeDeclarationIsRefused()
    {
        var file = WriteSettings("Capture_One", "CaptureOne.exe_StrongName_abc", "16.4.0.0", """
            <?xml version="1.0"?>
            <!DOCTYPE configuration [ <!ENTITY place "E:\Shoots\Weddings.cocatalog"> ]>
            <configuration><value>&place;</value></configuration>
            """);

        var documents = CaptureOneDocuments.Read(LocalAppData);

        Assert.Equal([file], documents.Unread);
        Assert.Empty(documents.Catalogs);
    }

    /// <summary>
    /// A settings folder that will not be listed may hold settings, so Capture One is found and the
    /// list is not complete, rather than "never used".
    /// </summary>
    [Fact]
    public void ASettingsFolderThatWillNotBeListedIsFoundAndUnread()
    {
        var company = Path.Combine(LocalAppData, "Capture_One");
        Directory.CreateDirectory(Path.Combine(company, "CaptureOne.exe_StrongName_abc"));

        using var denied = new DeniedDirectory(company);

        var documents = CaptureOneDocuments.Read(LocalAppData);

        Assert.True(documents.Found);
        Assert.False(documents.IsComplete);
        Assert.Equal([company], documents.Unread);
    }
}
