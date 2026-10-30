using System.Text.RegularExpressions;

namespace Deguffer.Core.Providers;

/// <summary>
/// One installed box version as <c>vagrant box prune</c> names it: the box's logical name, the
/// provider it was built for, and the version, each exactly as Vagrant printed it.
/// </summary>
/// <param name="Name">
/// The logical name, such as <c>hashicorp/bionic64</c>, which is not the folder's name: Vagrant maps
/// a slash and, on Windows, a colon to markers on disk. See <see cref="VagrantBoxLayout"/>.
/// </param>
internal sealed record VagrantBoxVersion(string Name, string Provider, string Version);

/// <summary>
/// What <c>vagrant box prune --dry-run</c> said it would keep and would remove.
/// </summary>
/// <param name="Kept">The newest version of each box for each provider, which prune never removes.</param>
/// <param name="Removed">
/// Every version prune would remove, in the order it would remove them. A version listed twice is a
/// box with two architectures for one provider, which the real run cannot remove: see
/// <see cref="VagrantSupersededBoxProvider"/>.
/// </param>
internal sealed record VagrantPrunePreview(
    IReadOnlyList<VagrantBoxVersion> Kept,
    IReadOnlyList<VagrantBoxVersion> Removed);

/// <summary>
/// Reads what <c>vagrant box prune --dry-run</c> printed.
///
/// <para><b>The output is text, and Vagrant offers nothing better here.</b>
/// <c>--machine-readable</c> turns every line into a <c>ui</c> record, but prune's own lines are
/// <c>@env.ui.info</c> calls either way, so it adds a timestamp and a comma encoding and no structure.
/// Redirected, Vagrant's output is <c>Vagrant::UI::Basic</c> (<c>bin/vagrant</c> chooses it when
/// standard output is not a terminal), whose <c>format_message</c> adds no prefix and no colour. The
/// lines below are therefore printed exactly as <c>plugins/commands/box/command/prune.rb</c> writes
/// them (hashicorp/vagrant at <c>dc55920a</c>).</para>
///
/// <para><b>Read by section, and every section is required.</b> Vagrant may print other lines first:
/// a checkpoint alert, a warning about a version folder it could not read, anything a user's
/// Vagrantfile writes. So a line is read only for the shape its section allows, and a report that
/// never reached its own last line answers null. The caller then offers nothing, because the only
/// other figure available is a measure of every box, the newest included.</para>
/// </summary>
internal static partial class VagrantPruneReport
{
    private const string KeptHeading = "The following boxes will be kept...";

    private const string RemovalHeading = "Checking for older boxes...";

    private const string NothingToRemove = "No old versions of boxes to remove...";

    /// <summary><c>vagrant.commands.box.no_installed_boxes</c> in <c>templates/locales/en.yml</c>.</summary>
    private const string NoInstalledBoxes = "There are no installed boxes! Use `vagrant box add` to add some.";

    /// <summary>
    /// <c>"#{name.ljust(longest_box_length)} (#{provider}, #{version})"</c>. The name is padded to the
    /// longest name, so it is read lazily up to the last <c> (</c> the rest of the line still fits
    /// after: a name may hold a parenthesis of its own, and the provider and version cannot.
    /// </summary>
    [GeneratedRegex(
        @"\A(?<name>\S.*?) +\((?<provider>[^\s,()]+), (?<version>" + VersionPattern + @")\)\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeptLine();

    /// <summary>
    /// <c>"Would remove #{name} #{provider} #{version}"</c>, read from the right: the provider and the
    /// version are single words, and a name may have spaces in it.
    /// </summary>
    [GeneratedRegex(
        @"\AWould remove (?<name>\S.*) (?<provider>[^\s,()]+) (?<version>" + VersionPattern + @")\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex RemovalLine();

    /// <summary>
    /// RubyGems' <c>Gem::Version::VERSION_PATTERN</c>, which <c>BoxCollection#all</c> requires of a
    /// version folder's name before it lists the box at all. Anything else in that place is not a
    /// version Vagrant would print.
    /// </summary>
    private const string VersionPattern = @"[0-9]+(?:\.[0-9a-zA-Z]+)*(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?";

    /// <summary>
    /// The preview, or null where the output is not a whole report.
    ///
    /// <para>A removal must name a box and provider whose newest version was listed as kept, and a
    /// different version of it: prune builds both lists from one table, so a removal with no kept
    /// counterpart is not prune's line, whatever it looks like.</para>
    /// </summary>
    public static VagrantPrunePreview? TryRead(string standardOutput)
    {
        var kept = new List<VagrantBoxVersion>();
        var removed = new List<VagrantBoxVersion>();
        var section = Section.Before;
        var finished = false;

        foreach (var raw in standardOutput.Split('\n'))
        {
            // Ruby's puts ends a line with a line feed, and Windows' text mode adds a carriage return.
            var line = raw.TrimEnd('\r');

            if (line == NoInstalledBoxes)
            {
                return new VagrantPrunePreview([], []);
            }

            switch (section)
            {
                case Section.Before when line == KeptHeading:
                    section = Section.Kept;
                    break;

                case Section.Kept when line == RemovalHeading:
                    section = Section.Removals;
                    break;

                case Section.Kept when KeptLine().Match(line) is { Success: true } match:
                    kept.Add(Read(match));
                    break;

                case Section.Removals when line == NothingToRemove:
                    finished = removed.Count == 0;
                    section = Section.After;
                    break;

                case Section.Removals when RemovalLine().Match(line) is { Success: true } match:
                    removed.Add(Read(match));
                    finished = true;
                    break;
            }
        }

        return finished && kept.Count > 0 && removed.All(version => Supersedes(kept, version))
            ? new VagrantPrunePreview(kept, removed)
            : null;
    }

    private static bool Supersedes(IReadOnlyList<VagrantBoxVersion> kept, VagrantBoxVersion removed) =>
        kept.Any(newest => newest.Name == removed.Name
            && newest.Provider == removed.Provider
            && newest.Version != removed.Version);

    private static VagrantBoxVersion Read(Match match) => new(
        match.Groups["name"].Value,
        match.Groups["provider"].Value,
        match.Groups["version"].Value);

    private enum Section
    {
        Before,
        Kept,
        Removals,
        After,
    }
}
