namespace Deguffer.Core.Retrim;

/// <summary>
/// Windows' own retrim of a volume: <c>MSFT_Volume.Optimize</c> with <c>ReTrim</c> and nothing else,
/// the step Optimize Drives and <c>Optimize-Volume -ReTrim</c> take. A seam so the run that asks for
/// it is tested without retrimming the machine running the suite.
/// </summary>
public interface IVolumeRetrim
{
    /// <summary>
    /// Ask Windows to retrim the volume Windows names <paramref name="volumeName"/>, in its
    /// <c>\?\Volume{GUID}\</c> form, and wait for its answer. Blocks for as long as Windows takes, so
    /// never call it on the UI thread.
    /// </summary>
    RetrimAnswer Retrim(string volumeName);
}
