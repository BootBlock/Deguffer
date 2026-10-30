namespace Deguffer.App.Controls;

/// <summary>
/// The offset a XAML element's visual takes on top of its layout once
/// <c>ElementCompositionPreview.SetIsTranslationEnabled</c> turns it on, named once for every animation
/// and property write that moves an element without laying it out again.
/// </summary>
internal static class VisualTranslation
{
    public const string Property = "Translation";
}
