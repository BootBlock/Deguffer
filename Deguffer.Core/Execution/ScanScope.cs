namespace Deguffer.Core.Execution;

/// <summary>
/// Which locations a Storage scan measures: every one Deguffer recognises, or only the ones the user
/// chose (<see cref="Configuration.ProviderChoice"/>).
///
/// <para>A chosen scan replaces the list as a full one does. A row it did not measure is not on
/// screen, so nothing can be ticked or cleaned on the strength of a measurement older than the
/// scan the user just ran.</para>
/// </summary>
public enum ScanScope
{
    Every = 0,
    Chosen = 1,
}
