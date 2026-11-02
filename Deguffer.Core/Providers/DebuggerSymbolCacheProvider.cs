using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The debugger symbol stores that only Microsoft's public symbol servers fill, which a symbol path names.
///
/// <para><b>Tier 1.</b> Microsoft's server and NuGet.org's keep every symbol they publish, so whatever is
/// removed is downloaded again the next time a debugger needs it, and Microsoft's own documentation says a
/// downstream store can be deleted at any time. After a Windows update most of a store is symbols for files
/// the machine no longer has.</para>
///
/// <para>Only a store whose every source the symbol path names is here. WinDbg's and Visual Studio's own
/// stores, whose servers Deguffer cannot read, are on <see cref="OtherServerSymbolCacheProvider"/>.</para>
/// </summary>
public sealed class DebuggerSymbolCacheProvider : DebuggerSymbolCacheProviderBase
{
    public DebuggerSymbolCacheProvider(
        IUserEnvironment? environment = null,
        IProcessRunner? runner = null,
        IProcessInspector? inspector = null,
        IDirectoryScanner? scanner = null,
        SymbolStoreDiscovery? discovery = null,
        ISystemDirectories? system = null,
        IVolumeInventory? volumes = null)
        : base(environment, runner, inspector, scanner, discovery, system, volumes)
    {
    }

    public override string Id => "debugger-symbols";

    public override string Name => "Debugger symbol cache";

    public override SafetyTier Tier => SafetyTier.RegenerableCache;

    public override string WhatHappensOnNextUse =>
        "The next time a debugger needs a symbol file, it downloads it again from Microsoft, so loading symbols "
        + "takes longer once. Your symbol path and debugger settings are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "The Windows debuggers: WinDbg, the console debuggers and Visual Studio",
        Publisher = "Microsoft",
        Purpose = "A debugger downloads a symbol file for each program and Windows file it looks at, and keeps a copy "
            + "in a symbol store so it never downloads it twice. Nothing removes the old copies, so the store grows "
            + "with every Windows update and every build debugged.",
        Recommendation = "These stores are filled only from Microsoft's public symbol servers, which keep every symbol "
            + "they publish, so a debugger downloads again whatever it needs. Deguffer removes only the downloaded "
            + "copies, and leaves the stores themselves.",
    };

    private protected override SymbolStoreFeed Feed => SymbolStoreFeed.Microsoft;

    private protected override string NoStore =>
        "No symbol path on this machine names a store filled only from Microsoft's public symbol servers.";
}
