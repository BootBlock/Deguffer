namespace Deguffer.App.ViewModels;

/// <summary>
/// The UI Automation ids of one Storage row's controls, each from the provider's stable id, so a
/// script reaches one location whatever its wording or its place in the order. The visible name
/// cannot: it changes with the wording. A re-plan keeps the provider, so it keeps these.
/// </summary>
public sealed class FindingAutomationIds(string providerId)
{
    public string Toggle { get; } = $"LocationToggle.{providerId}";

    /// <summary>The link that opens the row's items.</summary>
    public string ItemsLink { get; } = $"LocationItemsLink.{providerId}";

    /// <summary>The "What is this?" link, in either view.</summary>
    public string InformationLink { get; } = $"LocationInformationLink.{providerId}";
}
