namespace Deguffer.Core.Memory;

/// <summary>One running service and the process that hosts it.</summary>
/// <param name="Name">The service's key name, which is what identifies it.</param>
/// <param name="DisplayName">What Windows shows people it is called.</param>
/// <param name="ProcessId">The host process. Several services can share one.</param>
public sealed record RunningService(string Name, string DisplayName, int ProcessId);

/// <summary>How much of the service list one read obtained.</summary>
public enum ServiceListing
{
    /// <summary>Windows would not open its service list to this account at all.</summary>
    NotListed = 0,

    /// <summary>The list was opened, and the read stopped before its end.</summary>
    ListedInPart,

    /// <summary>
    /// The list was read to its end. Not every service: Windows leaves out, with no error, the services
    /// this account may not query, so their hosts look like ordinary processes (§7.2).
    /// </summary>
    Listed,
}

/// <summary>The running services one read obtained, and how much of the list that was.</summary>
public sealed record ServiceTable(IReadOnlyList<RunningService> Services, ServiceListing Listing);
