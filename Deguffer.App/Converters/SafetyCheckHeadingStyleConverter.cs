using Deguffer.Core.Execution;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Deguffer.App.Converters;

/// <summary>
/// The style of a clean's safety check heading: strong text, in the critical colour for a failure
/// alone. Spending that colour on a check that could not be completed would leave nothing to mark the
/// one verdict that asks the user to report a fault.
/// </summary>
public sealed partial class SafetyCheckHeadingStyleConverter : IValueConverter
{
    /// <summary>The <c>App.xaml</c> style for the heading of <paramref name="verdict"/>.</summary>
    public static string KeyFor(RunVerdict verdict) => verdict switch
    {
        RunVerdict.AllSurvived or RunVerdict.Unverified or RunVerdict.RemovedFromOutside => "SafetyCheckHeading",
        RunVerdict.VerificationFailed => "SafetyCheckFailedHeading",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };

    public object Convert(object value, Type targetType, object parameter, string language) =>
        (Style)Application.Current.Resources[KeyFor((RunVerdict)value)];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException("A safety check's heading style is display-only.");
}
