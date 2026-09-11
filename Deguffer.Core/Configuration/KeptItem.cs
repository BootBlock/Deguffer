using Deguffer.Core.Execution;

namespace Deguffer.Core.Configuration;

/// <summary>One item the user has asked Deguffer never to offer.</summary>
/// <param name="ProviderId">The provider whose item it is. Keys are unique within one provider only.</param>
/// <param name="ProviderName">
/// What that provider is called, stored for the same reason <see cref="ItemIdentity.Name"/> is: the
/// list of kept items has to say whose each one is on a page that has no scan behind it.
/// </param>
/// <param name="Item">The item, as its provider identifies it.</param>
public sealed record KeptItem(string ProviderId, string ProviderName, ItemIdentity Item);
