using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Deguffer.Core.Configuration;
using Deguffer.Core.Execution;
using Deguffer.Core.Execution.Sizes;
using Deguffer.Core.Execution.Timing;
using Deguffer.Core.Providers;
using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// Every stored type is read and written through a contract generated at build time, where it was once
/// built by reflection at run time. The files on users' machines were written by the reflection
/// contract, so the generated one has to read and write exactly what that one did: a member the
/// generator drops, or reads as its default, is a setting lost without a word.
///
/// <para>Each case compares the two resolvers directly, under the same options: the reflection
/// contract is the generated context's own options with the reflection resolver put back. Each sample
/// holds a value away from its default wherever the type allows one, so a member read as its default
/// fails.</para>
///
/// <para>What this cannot see is the options themselves, because both sides take them from the
/// context. A context whose naming or strictness drifted from what the stores used is caught by each
/// store's own tests, which read files written out by hand in the stored form.</para>
/// </summary>
public class StoreContractTests
{
    private static readonly JsonSerializerContext[] Contexts =
    [
        IndentedStoreJson.Default,
        CompactStoreJson.Default,
        StrictStoreJson.Default,
    ];

    private static readonly DateTime TakenUtc = new(2026, 11, 6, 9, 30, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<Sample> Samples =
    [
        new(IndentedStoreJson.Default.Folders, new[] { @"C:\Users\testuser\Emulators", null }),
        new(IndentedStoreJson.Default.FolderList, new[] { @"C:\Users\testuser\Emulators", @"D:\Games" }),
        new(IndentedStoreJson.Default.Selections, new Dictionary<string, RememberedSelection>
        {
            ["npm"] = new(IsSelected: false, new Dictionary<string, bool> { [@"C:\Users\testuser\src\node_modules"] = true }),
            ["pip"] = new(IsSelected: true, new Dictionary<string, bool>()),
        }),
        new(IndentedStoreJson.Default.SelectionMap, new Dictionary<string, RememberedSelection>
        {
            ["npm"] = new(IsSelected: true, new Dictionary<string, bool> { ["cache"] = false }),
        }),
        new(IndentedStoreJson.Default.SourceRootEntries, Elements(
            """["C:\\Users\\testuser\\src", {"path": "V:\\work", "remoteStorageApproved": true}, 7]""")),
        new(IndentedStoreJson.Default.SourceRoots, new[]
        {
            new SourceRootStore.StoredRoot(@"C:\Users\testuser\src", RemoteStorageApproved: false),
            new SourceRootStore.StoredRoot(@"V:\work", RemoteStorageApproved: true),
        }),
        new(IndentedStoreJson.Default.KeptItems, new[]
        {
            new KeptItem("docker", "Docker", new ItemIdentity("sha256:abc", "postgres:16")),
        }),
        new(IndentedStoreJson.Default.WindowMetrics, new WindowMetrics(new WindowBounds(412, 96, 1000, 700), IsMaximized: true)),
        new(IndentedStoreJson.Default.ToolWindowMetrics, new ToolWindowMetrics(new WindowBounds(40, 60, 420, 640), IsOpen: true)),
        new(CompactStoreJson.Default.RefusedPlaces, new Dictionary<string, string[]>
        {
            [@"C:\Users\testuser\AppData\Local\Temp"] = [@"C:\Users\testuser\AppData\Local\Temp\held.log"],
        }),
        new(CompactStoreJson.Default.ScanEstimates, new Dictionary<string, ScanEstimateCache.Entry>
        {
            [@"C:\Users\testuser\.nuget\packages"] = new(4096, 4000, IsApproximate: true, new DateTimeOffset(TakenUtc), Entries: 12),
        }),
        new(StrictStoreJson.Default.ProviderSizes, new ProviderSizeStore.Stored(1, new Dictionary<string, ProviderSizeStore.StoredPoint[]>
        {
            ["npm"] = [new(TakenUtc, Bytes: 2048, Entries: 3, Approximate: true, Ceiling: true, Elevated: true)],
        })),
        new(StrictStoreJson.Default.ComponentStoreStates, new ComponentStoreSettlement.Stored(1, new Dictionary<string, ComponentStoreSettlement.StoredState>
        {
            ["10.0.26100.1"] = new(ExplorerSize: 9, ActualSize: 8, SharedWithWindows: 7, BackupsAndDisabledFeatures: 6, CacheAndTemporaryData: 5, ReclaimablePackages: 4),
        })),
        new(StrictStoreJson.Default.PlanningTimes, new PlanTimeStore.Stored(1, new Dictionary<string, PlanTimeStore.StoredTime[]>
        {
            ["conda"] = [new(TakenUtc, Microseconds: 5_232_000, Elevated: true)],
        })),
    ];

    public static TheoryData<string> Contracts() => [.. Samples.Select(sample => sample.Name)];

    /// <summary>A contract declared with no sample here would go unchecked, so that is refused too.</summary>
    [Fact]
    public void EveryDeclaredContractHasASample()
    {
        var declared = Contexts
            .SelectMany(context => context.GetType().GetCustomAttributesData())
            .Where(attribute => attribute.AttributeType == typeof(JsonSerializableAttribute))
            .Select(attribute => (Type)attribute.ConstructorArguments[0].Value!)
            .Order(Comparer<Type>.Create((left, right) => string.CompareOrdinal(left.FullName, right.FullName)));

        var sampled = Samples
            .Select(sample => sample.Contract.Type)
            .Order(Comparer<Type>.Create((left, right) => string.CompareOrdinal(left.FullName, right.FullName)));

        Assert.Equal(declared, sampled);
    }

    /// <summary>The generated contract writes, byte for byte, what the reflection one wrote.</summary>
    [Theory]
    [MemberData(nameof(Contracts))]
    public void WritesWhatReflectionWrote(string contract)
    {
        var sample = Samples.Single(candidate => candidate.Name == contract);

        Assert.Equal(sample.ByReflection(), JsonSerializer.Serialize(sample.Value, sample.Contract));
    }

    /// <summary>
    /// The generated contract reads every member of a file the reflection one wrote. Compared by
    /// writing what was read back out through reflection, which states every member, rather than by
    /// record equality, which compares a dictionary member by reference.
    /// </summary>
    [Theory]
    [MemberData(nameof(Contracts))]
    public void ReadsWhatReflectionWrote(string contract)
    {
        var sample = Samples.Single(candidate => candidate.Name == contract);
        var written = sample.ByReflection();

        var read = JsonSerializer.Deserialize(written, sample.Contract);

        Assert.Equal(written, JsonSerializer.Serialize(read, sample.Contract.Type, sample.ReflectionOptions));
    }

    /// <summary>
    /// The placement store takes its contract by its type parameter, so a placement type with none on
    /// the context would fail as the window opens. Asked here instead.
    /// </summary>
    [Fact]
    public void EveryPlacementTypeHasAContract()
    {
        var placements = typeof(IWindowPlacement).Assembly.GetTypes()
            .Where(type => type.IsClass && typeof(IWindowPlacement).IsAssignableFrom(type))
            .ToList();

        Assert.NotEmpty(placements);

        foreach (var placement in placements)
        {
            var store = typeof(WindowPlacementStore<>).MakeGenericType(placement);
            var contract = store.GetField("Contract", BindingFlags.NonPublic | BindingFlags.Static)!;

            Assert.Same(IndentedStoreJson.Default.GetTypeInfo(placement), contract.GetValue(null));
        }
    }

    private static JsonElement[] Elements(string json)
    {
        using var document = JsonDocument.Parse(json);

        return [.. document.RootElement.EnumerateArray().Select(element => element.Clone())];
    }

    /// <summary>One stored type's generated contract, and a value of it to write.</summary>
    private sealed record Sample(JsonTypeInfo Contract, object Value)
    {
        public string Name => Contract.Type.ToString();

        /// <summary>The generated context's own options, resolved by reflection as every store once was.</summary>
        public JsonSerializerOptions ReflectionOptions { get; } =
            new(Contract.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

        public string ByReflection() => JsonSerializer.Serialize(Value, Contract.Type, ReflectionOptions);
    }
}
