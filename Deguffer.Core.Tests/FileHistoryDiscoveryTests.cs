using Deguffer.Core.Providers;
using Deguffer.Core.Tests.Fakes;

namespace Deguffer.Core.Tests;

/// <summary>
/// Finding the drive Windows is currently sending File History to.
///
/// <para>This is the half of the provider carrying an assumption about a layout Microsoft has never
/// documented, which is why it is tested apart from what the provider does with the answer. The
/// question it has to get right is not "is there a FileHistory folder somewhere" — a machine that
/// has changed backup drives keeps a complete, stale one on the old drive — but "which one is
/// assigned", and the configuration in the profile is the only thing on the machine that says.</para>
///
/// <para>Everything runs against a synthetic profile and a synthetic drive, so nothing here depends
/// on whether the developer has ever switched File History on.</para>
/// </summary>
public sealed class FileHistoryDiscoveryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeUserEnvironment _environment;

    public FileHistoryDiscoveryTests() => _environment = new FakeUserEnvironment(_temp.Path);

    public void Dispose() => _temp.Dispose();

    private FileHistoryDiscovery Discovery() => new(_environment);

    /// <summary>A drive root under the scratch tree, standing in for a backup disk.</summary>
    private string CreateDrive(string name) => _temp.CreateDirectory("drives", name);

    /// <summary>This machine's history on <paramref name="drive"/>, laid out as Windows lays it out.</summary>
    private string CreateHistory(string drive) => _temp.CreateDirectory(
        Path.GetRelativePath(_temp.Path, drive),
        "FileHistory",
        FakeUserEnvironment.Account,
        FakeUserEnvironment.Machine,
        "Data");

    private void WriteConfiguration(string xml, string name = "Config.xml")
    {
        var directory = Path.Combine(
            _environment.LocalAppData, "Microsoft", "Windows", "FileHistory", "Configuration");

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), xml);
    }

    private static string NamingTarget(string path) =>
        $"<DataProtectionConfig><Target><Url>{path}</Url></Target></DataProtectionConfig>";

    [Fact]
    public void ReportsNotConfiguredWhenTheProfileHoldsNoFileHistorySettings()
    {
        Assert.False(Discovery().IsConfigured);
        Assert.Equal(FileHistoryLookup.NotConfigured, Discovery().Locate().Outcome);
    }

    [Fact]
    public void FindsTheDriveTheConfigurationNames()
    {
        var drive = CreateDrive("E");
        var data = CreateHistory(drive);
        WriteConfiguration(NamingTarget(drive));

        var located = Discovery().Locate();

        Assert.Equal(FileHistoryLookup.Found, located.Outcome);
        Assert.Equal(data, located.Target!.DataDirectory);
    }

    /// <summary>
    /// The configuration may name the drive, the folder holding every account's history, this
    /// account's folder, or this machine's. All four describe the same target, and each is reduced
    /// to the drive so that <see cref="FileHistoryTarget"/> rebuilds the same four folders whichever
    /// arrived.
    /// </summary>
    [Theory]
    [InlineData("FileHistory")]
    [InlineData("FileHistory/" + FakeUserEnvironment.Account)]
    [InlineData("FileHistory/" + FakeUserEnvironment.Account + "/" + FakeUserEnvironment.Machine)]
    public void FindsTheDriveWhateverDepthTheConfigurationNames(string suffix)
    {
        var drive = CreateDrive("E");
        var data = CreateHistory(drive);
        WriteConfiguration(NamingTarget(Path.Combine(drive, suffix.Replace('/', Path.DirectorySeparatorChar))));

        Assert.Equal(data, Discovery().Locate().Target!.DataDirectory);
    }

    /// <summary>
    /// The schema is undocumented, so no element or attribute name is matched. A configuration
    /// naming its target somewhere this code has never heard of still has to be read, because the
    /// alternative is a parser written against a guess that fails silently when the guess is wrong.
    /// </summary>
    [Fact]
    public void ReadsATargetFromAnElementItHasNeverHeardOf()
    {
        var drive = CreateDrive("E");
        var data = CreateHistory(drive);
        WriteConfiguration($"<Whatever><SomethingElse Where=\"{drive}\" /></Whatever>");

        Assert.Equal(data, Discovery().Locate().Target!.DataDirectory);
    }

    /// <summary>
    /// Windows keeps the configuration as a pair and swaps between them, so the newest is the one
    /// in force. A machine that has moved its backup to another drive is exactly the case this has
    /// to get right, and getting it wrong would preview one drive and trim another.
    /// </summary>
    [Fact]
    public void PrefersTheNewestConfiguration()
    {
        var old = CreateDrive("D");
        var current = CreateDrive("E");
        CreateHistory(old);
        var data = CreateHistory(current);

        WriteConfiguration(NamingTarget(old), "Config1.xml");
        WriteConfiguration(NamingTarget(current), "Config2.xml");

        File.SetLastWriteTimeUtc(
            Path.Combine(_environment.LocalAppData, "Microsoft", "Windows", "FileHistory", "Configuration", "Config1.xml"),
            DateTime.UtcNow - TimeSpan.FromDays(30));

        Assert.Equal(data, Discovery().Locate().Target!.DataDirectory);
    }

    [Fact]
    public void ReportsNoTargetWhenTheNamedDriveHoldsNothing()
    {
        var drive = CreateDrive("E");
        WriteConfiguration(NamingTarget(drive));

        Assert.Equal(FileHistoryLookup.TargetNotFound, Discovery().Locate().Outcome);
    }

    [Fact]
    public void ReportsNoTargetWhenTheConfigurationNamesNoPathAtAll()
    {
        CreateHistory(CreateDrive("E"));
        WriteConfiguration("<DataProtectionConfig><Target><Url>not a path</Url></Target></DataProtectionConfig>");

        Assert.Equal(FileHistoryLookup.TargetNotFound, Discovery().Locate().Outcome);
    }

    /// <summary>
    /// A configuration Windows was part-way through writing, or one somebody replaced. Nothing is
    /// offered rather than a target invented, and the app carries on planning every other row.
    /// </summary>
    [Fact]
    public void ReportsNoTargetWhenTheConfigurationIsNotXml()
    {
        CreateHistory(CreateDrive("E"));
        WriteConfiguration("<DataProtectionConfig><Target>");

        Assert.Equal(FileHistoryLookup.TargetNotFound, Discovery().Locate().Outcome);
    }

    /// <summary>
    /// A relative value cannot name a drive, and resolving one would resolve it against Deguffer's
    /// own working directory — a folder nobody pointed at.
    /// </summary>
    [Fact]
    public void RefusesAValueThatIsNotFullyQualified()
    {
        CreateHistory(CreateDrive("E"));
        WriteConfiguration(NamingTarget(@"drives\E"));

        Assert.Equal(FileHistoryLookup.TargetNotFound, Discovery().Locate().Outcome);
    }

    /// <summary>
    /// A value already in §6.3's extended-length form is read rather than refused. Windows names a
    /// drive that has no letter this way, so refusing the prefix outright would lose exactly the
    /// case a File History target most often is — an external disk with no assigned letter.
    ///
    /// <para><b>What a fixture cannot reach is the volume-GUID form itself</b>, because that names a
    /// real volume on the machine. The handling that form needs is in
    /// <see cref="Safety.LongPath.Display"/>, and <c>LongPathTests</c> covers it there as pure string
    /// work — which is where the defect lived, and where a real volume would make it no more
    /// visible.</para>
    /// </summary>
    [Fact]
    public void ReadsATargetAlreadyInExtendedLengthForm()
    {
        var drive = CreateDrive("E");
        var data = CreateHistory(drive);
        WriteConfiguration(NamingTarget(@"\\?\" + drive + @"\"));

        Assert.Equal(data, Discovery().Locate().Target!.DataDirectory);
    }

    /// <summary>
    /// A relative segment is resolved rather than carried, because <see cref="Safety.LongPath.Extended"/>
    /// requires an already-normalised path — the Win32 device namespace resolves nothing itself, so a
    /// <c>..</c> that survived here would reach a folder nobody named (§6.3).
    /// </summary>
    [Fact]
    public void ResolvesARelativeSegmentInAConfiguredPath()
    {
        var drive = CreateDrive("E");
        var data = CreateHistory(drive);
        WriteConfiguration(NamingTarget(Path.Combine(drive, "sub", "..")));

        Assert.Equal(data, Discovery().Locate().Target!.DataDirectory);
    }

    /// <summary>
    /// G4: the answer costs an XML parse and several existence checks, and the provider asks for it
    /// while deciding presence, while declaring its tool roots, and again while planning. It is held
    /// for the pass and dropped when the pass says so, which is what lets a drive plugged in while
    /// the app was open be seen.
    /// </summary>
    [Fact]
    public void RemembersTheAnswerUntilItIsInvalidated()
    {
        var drive = CreateDrive("E");
        WriteConfiguration(NamingTarget(drive));

        var discovery = Discovery();
        Assert.Equal(FileHistoryLookup.TargetNotFound, discovery.Locate().Outcome);

        CreateHistory(drive);
        Assert.Equal(FileHistoryLookup.TargetNotFound, discovery.Locate().Outcome);

        discovery.Invalidate();
        Assert.Equal(FileHistoryLookup.Found, discovery.Locate().Outcome);
    }
}
