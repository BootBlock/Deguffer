using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests;

/// <summary>
/// §7.6: the handle that can pass an ATA command, and the route that sends one, are held to the types
/// that are allowed them, so the only commands a drive can be sent are the ones in the table.
/// </summary>
public sealed partial class DriveHealthSourcesTests
{
    [Fact]
    public void OnlyTheDriveProbeOpensADiskForCommands()
    {
        Assert.Equal(["DriveProbe.cs"], Callers(OpenForCommandsCall()));
    }

    [Fact]
    public void OnlyTheProbeAndTheStorageQueriesSendAControlCode()
    {
        Assert.Equal(["DriveProbe.cs", "StorageQueries.cs"], Callers(QueryCall()));
    }

    private static IReadOnlyList<string> Callers(Regex call) =>
        [.. ProductSources.Files()
            .Where(file => Path.GetFileName(file) != "DeviceCalls.cs")
            .Where(file => ProductSources.Code(File.ReadAllText(file)).Any(call.IsMatch))
            .Select(file => Path.GetFileName(file)!)
            .Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\bOpenForCommands\s*\(")]
    private static partial Regex OpenForCommandsCall();

    [GeneratedRegex(@"\bDeviceCalls\.Query\s*\(")]
    private static partial Regex QueryCall();
}
