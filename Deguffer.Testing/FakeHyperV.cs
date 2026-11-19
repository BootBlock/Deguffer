using System.Collections.Concurrent;
using System.Globalization;
using System.Security;
using Deguffer.Core.VirtualDisks;
using Deguffer.Core.Wmi;

namespace Deguffer.Testing;

/// <summary>
/// Hyper-V's WMI namespace as a test records it: the rows <c>Msvm_ComputerSystem</c> and
/// <c>Msvm_VirtualSystemSettingData</c> give, and what <c>GetVirtualHardDiskSettingData</c> gives for each
/// disk, in the CIM-XML form Hyper-V embeds it in. The real namespace lists the machines of whoever runs
/// the suite.
///
/// <para>A disk nobody recorded is answered as Hyper-V answers a file it cannot find, so a test that
/// forgets one sees the chain cut short rather than a chain that ends quietly.</para>
/// </summary>
public sealed class FakeHyperV : IHyperVManagement, IHyperVSession
{
    /// <summary><c>GetVirtualHardDiskSettingData</c>'s "File not found".</summary>
    public const int FileNotFound = 32779;

    private readonly List<HyperVSystemRow> _systems = [];
    private readonly List<HyperVSettingsRow> _settings = [];
    private readonly Dictionary<string, HyperVDiskAnswer> _disks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _described = new();
    private int _opens;
    private int _disposals;

    /// <summary>The code the namespace fails to open with, or <c>S_OK</c> for a namespace that opens.</summary>
    public int OpenError { get; init; } = WbemConnection.S_OK;

    /// <summary>The code the listing of machines fails with, or <c>S_OK</c>.</summary>
    public int SystemsError { get; init; } = WbemConnection.S_OK;

    /// <summary>The code the listing of settings fails with, or <c>S_OK</c>.</summary>
    public int SettingsError { get; init; } = WbemConnection.S_OK;

    /// <summary>Run as each disk is described, before the answer, so a test can hold the reading there.</summary>
    public Action<string, CancellationToken>? Describing { get; init; }

    /// <summary>How many sessions were opened, so a test can prove Hyper-V was never asked.</summary>
    public int Opens => Volatile.Read(ref _opens);

    /// <summary>How many sessions were released.</summary>
    public int Disposals => Volatile.Read(ref _disposals);

    /// <summary>Each disk described, in order, so a test can hold a reading to one question a disk.</summary>
    public IReadOnlyList<string> Described => [.. _described];

    /// <summary>
    /// A realized machine: its system, its active settings naming <paramref name="attached"/>, and a
    /// checkpoint's settings for each of <paramref name="checkpoints"/>, each naming the disks it keeps.
    /// Every disk named is recorded as built on nothing unless the test recorded it already.
    /// </summary>
    public FakeHyperV Machine(string name, int enabledState, IReadOnlyList<string> attached, params IReadOnlyList<string>[] checkpoints)
    {
        var id = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant();

        _systems.Add(new HyperVSystemRow(id, name, enabledState));
        _settings.Add(new HyperVSettingsRow(id, "Microsoft:Hyper-V:System:Realized", HardDisks(attached)));

        foreach (var kept in checkpoints)
        {
            _settings.Add(new HyperVSettingsRow(id, "Microsoft:Hyper-V:Snapshot:Realized", HardDisks(kept)));
        }

        foreach (var disk in attached.Concat(checkpoints.SelectMany(kept => kept)))
        {
            _disks.TryAdd(disk, Recorded(disk, null));
        }

        return this;
    }

    /// <summary>A system row as given, such as the host's, which no settings name.</summary>
    public FakeHyperV System(HyperVSystemRow row)
    {
        _systems.Add(row);
        return this;
    }

    /// <summary>A settings row as given, such as a planned machine's, or one naming a DVD image.</summary>
    public FakeHyperV Settings(HyperVSettingsRow row)
    {
        _settings.Add(row);
        return this;
    }

    /// <summary>The disk at <paramref name="path"/>, described as built on <paramref name="parent"/>, or on nothing.</summary>
    public FakeHyperV Disk(string path, string? parent = null) => Answering(path, Recorded(path, parent));

    /// <summary>The disk at <paramref name="path"/>, answered with <paramref name="answer"/>.</summary>
    public FakeHyperV Answering(string path, HyperVDiskAnswer answer)
    {
        _disks[path] = answer;
        return this;
    }

    /// <summary>
    /// The embedded <c>Msvm_VirtualHardDiskSettingData</c> for a dynamic VHDX disk, as CIM-XML: one
    /// <c>PROPERTY</c> a property, in Hyper-V's order, and a <c>ParentPath</c> with no <c>VALUE</c> for a disk
    /// built on nothing.
    /// </summary>
    public static string SettingData(string path, string? parent) =>
        "<INSTANCE CLASSNAME=\"Msvm_VirtualHardDiskSettingData\">"
        + Property("BlockSize", "uint32", "33554432")
        + Property("Caption", "string", "Virtual Hard Disk Setting Data")
        + Property("Format", "uint16", "3")
        + Property("InstanceID", "string", "Microsoft:" + SecurityElement.Escape(path))
        + Property("MaxInternalSize", "uint64", "136365211648")
        + Property("ParentIdentifier", "string", null)
        + Property("ParentPath", "string", parent is null ? null : SecurityElement.Escape(parent))
        + Property("ParentTimestamp", "datetime", null)
        + Property("Path", "string", SecurityElement.Escape(path))
        + Property("Type", "uint16", parent is null ? "3" : "4")
        + "</INSTANCE>";

    public IHyperVSession? Open(out int hr)
    {
        Interlocked.Increment(ref _opens);
        hr = OpenError;
        return hr == WbemConnection.S_OK ? this : null;
    }

    public IReadOnlyList<HyperVSystemRow>? Systems(out int hr, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        hr = SystemsError;
        return hr == WbemConnection.S_OK ? [.. _systems] : null;
    }

    public IReadOnlyList<HyperVSettingsRow>? Settings(out int hr, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        hr = SettingsError;
        return hr == WbemConnection.S_OK ? [.. _settings] : null;
    }

    public HyperVDiskAnswer DescribeDisk(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _described.Enqueue(path);
        Describing?.Invoke(path, ct);
        return _disks.TryGetValue(path, out var answer) ? answer : new HyperVDiskAnswer(WbemConnection.S_OK, FileNotFound, null);
    }

    public void Dispose() => Interlocked.Increment(ref _disposals);

    private static HyperVDiskAnswer Recorded(string path, string? parent) =>
        new(WbemConnection.S_OK, 0, SettingData(path, parent));

    private static HyperVStorageRow[] HardDisks(IEnumerable<string> paths) =>
        [.. paths.Select(path => new HyperVStorageRow("Microsoft:Hyper-V:Virtual Hard Disk", path))];

    private static string Property(string name, string type, string? value) =>
        $"<PROPERTY NAME=\"{name}\" TYPE=\"{type}\">" + (value is null ? string.Empty : $"<VALUE>{value}</VALUE>") + "</PROPERTY>";
}
