namespace Deguffer.Core.Memory;

/// <summary>
/// What a process that hosts services is called, and which services that is (§7.2).
///
/// <para>A host is named by what it holds, which is what a reader recognises: several hosts share
/// one image name, and the service is the part they came for. A host of several cannot carry every
/// name in a shape's label, which the picture trims to the width of the shape, so
/// <see cref="Name"/> says how many and <see cref="Holds"/> names them where the reader points at
/// one. Both halves are here, so neither can promise what the other does not say.</para>
///
/// <para>In Core rather than in the shell for the reason <see cref="MemoryPartGuide"/> is: §7.2
/// makes this a rule rather than presentation, and a rule is testable (G8).</para>
/// </summary>
public static class ServiceHostText
{
    /// <summary>
    /// What to call the process hosting <paramref name="services"/>, from its image name. The image
    /// name alone where it hosts none, which is every process in <em>Applications</em>.
    /// </summary>
    public static string Name(string image, IReadOnlyList<RunningService> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.Count switch
        {
            0 => image,
            1 => $"{image}: {services[0].DisplayName}",
            _ => $"{image}: {services.Count} services",
        };
    }

    /// <summary>
    /// The services a host holds, named, or nothing where <see cref="Name"/> has already named them:
    /// a host of one reads as that service, and a process hosting none is not a host.
    /// </summary>
    public static string Holds(IReadOnlyList<RunningService> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.Count < 2 ? string.Empty : $"It holds {Join(services)}.";
    }

    /// <summary>Service names for a sentence, in the form a reader expects rather than comma-separated throughout.</summary>
    private static string Join(IReadOnlyList<RunningService> services) =>
        string.Join(", ", services.Take(services.Count - 1).Select(s => s.DisplayName))
        + " and "
        + services[^1].DisplayName;
}
