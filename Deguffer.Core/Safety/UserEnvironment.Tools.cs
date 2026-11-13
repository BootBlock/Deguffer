namespace Deguffer.Core.Safety;

public partial interface IUserEnvironment
{
    /// <summary>
    /// Every variable a tool Deguffer starts is given, as <see cref="GetToolEnvironmentVariable"/>
    /// reads each one: this process's own block, which <see cref="ProcessRunner"/> hands on.
    ///
    /// <para>Exists for what a remembered tool answer is compared by
    /// (<see cref="Providers.ToolFingerprint"/>). npm, pnpm and Yarn expand any variable named in their
    /// configuration, so the variables an answer depends on cannot be listed by name.</para>
    /// </summary>
    IReadOnlyDictionary<string, string> ToolEnvironment { get; }
}

/// <summary>The environment the tools this process starts are given.</summary>
public sealed partial class UserEnvironment
{
    /// <summary>
    /// Read each time rather than kept: Windows never changes this process's block, but nothing stops
    /// code in it from setting a variable, and a tool started after that is given the new value.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToolEnvironment => ProcessEnvironment();
}
