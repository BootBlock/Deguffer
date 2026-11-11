namespace Deguffer.Core.Retrim;

/// <summary>
/// The switches <c>MSFT_Volume.Optimize</c> is given: <c>ReTrim</c>, and every other one it takes,
/// switched off.
///
/// <para>Written in full rather than leaving the rest unset, because a provider is free to read an
/// unset switch as it likes. <c>Defrag</c> moves every file on a volume, and <c>SlabConsolidate</c> and
/// <c>TierOptimize</c> move data between slabs and tiers. A clean has asked for none of them, and
/// Deguffer never defragments, on any medium.</para>
/// </summary>
public static class RetrimParameters
{
    /// <summary>Each input of <c>Optimize</c> by its documented name, with the value it is given.</summary>
    public static IReadOnlyList<(string Name, bool Value)> All { get; } =
    [
        ("ReTrim", true),
        ("Analyze", false),
        ("Defrag", false),
        ("SlabConsolidate", false),
        ("TierOptimize", false),
    ];
}
