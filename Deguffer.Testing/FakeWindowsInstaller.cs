using Deguffer.Core.InstalledApps;

namespace Deguffer.Testing;

/// <summary>Windows Installer answering whatever a test says about each product code.</summary>
public sealed class FakeWindowsInstaller : IWindowsInstaller
{
    private readonly Dictionary<Guid, InstallerProductState> _states = [];

    /// <summary>How many times each product code was asked about.</summary>
    public Dictionary<Guid, int> Queries { get; } = [];

    public FakeWindowsInstaller With(Guid code, InstallerProductState state)
    {
        _states[code] = state;
        return this;
    }

    /// <summary>What <see cref="QueryPatches"/> answers: no patches, listed in full, unless a test says otherwise.</summary>
    public InstallerPatches Patches { get; set; } = new(new HashSet<Guid>(), IsComplete: true);

    /// <summary>How many times the patches were listed.</summary>
    public int PatchQueries { get; private set; }

    public InstallerPatches QueryPatches()
    {
        PatchQueries++;
        return Patches;
    }

    /// <summary>An unnamed code is one Windows Installer does not know, as on a real machine.</summary>
    public InstallerProductState QueryProductState(Guid productCode)
    {
        Queries[productCode] = Queries.GetValueOrDefault(productCode) + 1;
        return _states.GetValueOrDefault(productCode, InstallerProductState.Unknown);
    }
}
