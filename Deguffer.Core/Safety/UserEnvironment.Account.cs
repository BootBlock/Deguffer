using System.Security.Principal;

namespace Deguffer.Core.Safety;

/// <summary>The account this process runs as, and what its token allows.</summary>
public sealed partial class UserEnvironment
{
    /// <summary>S-1-5-32-578, the well-known identifier of Hyper-V Administrators.</summary>
    private static readonly SecurityIdentifier HyperVAdministrators = new("S-1-5-32-578");

    /// <summary>
    /// Read once for the process, because a token's groups never change, and shared because Explore builds
    /// a fresh instance for every policy build. The two roles are the ones Hyper-V itself checks.
    /// </summary>
    private static readonly Lazy<bool> HyperVAdministrator = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator) || principal.IsInRole(HyperVAdministrators);
    });

    // Read once rather than through Invalidate: a process cannot change the account it runs as,
    // and relaunching elevated makes a new process with the same identity.
    public string? UserSecurityIdentifier { get; } = WindowsIdentity.GetCurrent().User?.Value;

    public string UserName { get; } = Environment.UserName;

    public string DomainName { get; } = Environment.UserDomainName;

    public bool IsAdministrator => Environment.IsPrivilegedProcess;

    public bool IsHyperVAdministrator => HyperVAdministrator.Value;
}
