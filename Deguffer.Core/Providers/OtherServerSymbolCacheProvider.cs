using Deguffer.Core.Safety;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Providers;

/// <summary>
/// The debugger symbol stores filled from a server that is not Microsoft's, or from servers Deguffer cannot
/// read: WinDbg's own store, <c>%PROGRAMDATA%\Dbg\sym</c>, and Visual Studio's, in the temporary folder
/// unless its settings move it.
///
/// <para><b>Tier 2.</b> A debugger downloads again whatever its servers still keep. A company's own symbol
/// server, or a build's share, may no longer keep the symbols of an old build, and then the copy here is the
/// only one left. WinDbg and Visual Studio keep their list of servers where Deguffer cannot read it, so their
/// stores are treated the same way, though both fetch only from Microsoft unless they are told
/// otherwise.</para>
///
/// <para><b>Visual Studio's store is in the temporary folder</b>, where the temporary-files row would take
/// its entries by age. This row claims the whole store instead, so it is offered once, by what it is.</para>
/// </summary>
public sealed class OtherServerSymbolCacheProvider : DebuggerSymbolCacheProviderBase
{
    public OtherServerSymbolCacheProvider(
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

    public override string Id => "debugger-symbols-other-servers";

    public override string Name => "Debugger symbol cache, other servers";

    public override SafetyTier Tier => SafetyTier.RegenerableWithCost;

    public override string WhatHappensOnNextUse =>
        "The next time a debugger needs a symbol file, it downloads it again from the server it came from. A "
        + "server that no longer keeps an old build's symbols cannot supply them again. Your symbol path and "
        + "debugger settings are untouched.";

    public override ProviderDescription Description { get; } = new()
    {
        Category = ProviderCategory.DeveloperTools,
        Application = "The Windows debuggers: WinDbg, the console debuggers and Visual Studio",
        Publisher = "Microsoft",
        Purpose = "A debugger downloads a symbol file for each program and Windows file it looks at, and keeps a copy "
            + "in a symbol store so it never downloads it twice. WinDbg and Visual Studio each keep a store of their "
            + "own, and a symbol path can name more. Nothing removes the old copies.",
        Recommendation = "These stores are filled from a server that is not Microsoft's, or from servers Deguffer cannot "
            + "see. Most of what is in them can be downloaded again, but a private server may no longer keep the symbols "
            + "of an old build. Remove them once you no longer debug builds whose symbols only this machine has.",
    };

    private protected override SymbolStoreFeed Feed => SymbolStoreFeed.Other;

    private protected override string NoStore =>
        "Neither WinDbg nor Visual Studio has kept a symbol store on this machine, and no symbol path names one "
        + "filled from another server.";
}
