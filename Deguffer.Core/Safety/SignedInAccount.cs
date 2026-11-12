namespace Deguffer.Core.Safety;

/// <summary>
/// The account signed in to a Windows session, as Windows names it for that session.
/// </summary>
/// <param name="Name">
/// <c>DOMAIN\UserName</c>, the domain being the machine's own name for a local account. Shown on screen
/// as the account the user signed in with.
/// </param>
/// <param name="SecurityIdentifier">
/// The account's identifier, or null where Windows would not turn the name into one. Compared before
/// the name wherever both accounts have one, because a name can be written more than one way and an
/// identifier cannot.
/// </param>
public sealed record SignedInAccount(string Name, string? SecurityIdentifier);
