using System.Globalization;

namespace Deguffer.App.Tests;

/// <summary>
/// The current culture set for the length of a test and put back after it.
///
/// <para>A label worded in one culture reads differently in another, which makes a culture change
/// the way to see whether something was worded again: a label still in the first culture's format
/// after the change was not.</para>
/// </summary>
internal sealed class CultureSwap : IDisposable
{
    private readonly CultureInfo _before = CultureInfo.CurrentCulture;

    public CultureSwap(string name) => To(name);

    public void To(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

    public void Dispose() => CultureInfo.CurrentCulture = _before;
}
