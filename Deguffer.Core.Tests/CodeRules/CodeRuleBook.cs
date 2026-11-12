using System.Text.RegularExpressions;

namespace Deguffer.Core.Tests.CodeRules;

/// <summary>
/// The patterns Deguffer's product source must not grow. Each names what new code does instead; the
/// memory note <i>Deguffer code rules the source sweep enforces</i> gives each rule's reason.
///
/// <para>A rule is here only when a pattern finds its breaches without judgement. A rule that needs
/// judgement, such as a type switch where a virtual member belongs, is a line in
/// <c>.claude/review-rules.md</c> instead.</para>
/// </summary>
internal static class CodeRuleBook
{
    private const string Core = "Deguffer.Core";
    private const string App = "Deguffer.App";
    private const string Benchmark = "Deguffer.Benchmark";

    private static readonly string[] Product = [Core, App, Benchmark];
    private static readonly string[] CoreAndApp = [Core, App];

    private const string StepTypes =
        "RunCommandStep|DeleteStep|DeleteDirectoryStep|ClearDirectoryStep|DeleteFileStep|DiskCleanupStep"
        + "|EmptyRecycleBinStep|ReleaseLocalCopiesStep|RemoveProfileStep|RemoveRestorePointsStep";

    public static IReadOnlyList<CodeRule> All { get; } =
    [
        new(
            "no-region",
            Pattern(@"^[ \t]*#(?:region|endregion)\b"),
            SourceView.Code,
            Product,
            "Split the file at the seam the region marks (G3).",
            "#region Helpers"),
        new(
            "no-warning-suppression",
            Pattern(@"^[ \t]*#pragma[ \t]+warning[ \t]+disable\b|\[\s*(?:[\w.]+\.)?SuppressMessage(?:Attribute)?\s*\("),
            SourceView.Code,
            Product,
            "Fix what the analyser reports. TreatWarningsAsErrors is on for a reason.",
            "#pragma warning disable CA1822"),
        new(
            "no-dynamic",
            Pattern(@"\bdynamic\b"),
            SourceView.Code,
            Product,
            "Use a typed model.",
            "dynamic reply = Read();"),
        new(
            "regex-is-generated",
            Pattern(@"\bnew\s+Regex\s*\(|\bRegex\s*\.\s*(?:IsMatch|Match|Matches|Replace|Split|Count|EnumerateMatches)\s*\("),
            SourceView.Code,
            Product,
            "Declare a [GeneratedRegex] partial method, built once (G5).",
            "var pattern = new Regex(text);"),
        new(
            "enumerate-not-get",
            Pattern(@"\.\s*(?:GetFiles|GetDirectories|GetFileSystemEntries|GetFileSystemInfos)\s*\("),
            SourceView.Code,
            Product,
            "Use the EnumerateX form, or the walk types, through LongPath (G4, §6.3).",
            "var files = Directory.GetFiles(path);"),
        new(
            "no-placeholder-comment",
            Pattern(@"\b(?:TODO|FIXME|HACK)\b"),
            SourceView.Everything,
            Product,
            "Do the work, or open an issue and leave the code saying nothing.",
            "// TODO: handle the refusal"),
        new(
            "known-folders-through-the-seam",
            Pattern(@"\bEnvironment\s*\.\s*(?:GetFolderPath|SpecialFolder)\b"),
            SourceView.Code,
            Product,
            "Ask IUserEnvironment or ISystemDirectories (G1).",
            "var home = Environment.GetFolderPath(folder);"),
        new(
            "processes-through-the-seam",
            Pattern(@"\bProcess\s*\.\s*(?:Start|GetProcesses|GetProcessesByName|GetProcessById)\s*\("),
            SourceView.Code,
            Product,
            "Use IProcessRunner or IProcessInspector (G1).",
            "Process.Start(\"tool.exe\");"),
        new(
            "registry-through-a-seam",
            Pattern(@"\bRegistry\s*\.\s*(?:CurrentUser|LocalMachine|Users|ClassesRoot|CurrentConfig|PerformanceData|GetValue|SetValue)\b|\bRegistryKey\s*\.\s*OpenBaseKey\s*\("),
            SourceView.Code,
            Product,
            "Read the registry through the seam that owns the key, so a test can fake it (G1).",
            "using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);"),
        new(
            "clock-through-time-provider",
            Pattern(@"\bDateTime(?:Offset)?\s*\.\s*(?:Now|UtcNow|Today)\b"),
            SourceView.Code,
            Product,
            "Take a TimeProvider, so an age cut-off can be tested at its edge.",
            "var age = DateTime.UtcNow - written;"),
        new(
            "no-sync-over-async",
            Pattern(@"\.\s*GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\("),
            SourceView.Code,
            Product,
            "Await it, or memoise the answer before the synchronous caller reads it (G4).",
            "var roots = ReadAsync(ct).GetAwaiter().GetResult();"),
        new(
            "catch-says-what-it-expects",
            Pattern(@"\bcatch\s*\(\s*(?:System\s*\.\s*)?Exception(?:\s+\w+)?\s*\)(?!\s*when\b)|\bcatch\s*(?=\{)"),
            SourceView.Code,
            Product,
            "Catch the exceptions you expect, with a when filter, and say why (G3).",
            "catch (Exception ex) { Log(ex); }"),
        new(
            "async-void-only-for-events",
            Pattern(@"\basync\s+void\s+\w+\s*\((?!\s*object\??\s+\w+\s*,)(?![^)]*EventArgs\b)"),
            SourceView.Code,
            Product,
            "Return a Task. Only an event handler is async void.",
            "private async void Refresh()"),
        new(
            "library-import-not-dll-import",
            Pattern(@"\[\s*(?:[\w.]+\.)?DllImport(?:Attribute)?\s*\("),
            SourceView.Code,
            Product,
            "Declare it with [LibraryImport] in the one native class for its library.",
            "[DllImport(\"kernel32.dll\")]"),
        new(
            "win32-errors-named-once",
            Pattern(@"\bconst\s+(?:int|uint|long|ulong)\s+(?:Error[A-Z]\w*|ERROR_\w+)\s*="),
            SourceView.Code,
            Product,
            "Use the shared Win32 error constants; never declare an error code locally. If it does not exist yet, create it in this change, or ask.",
            "private const int ErrorAccessDenied = 5;"),
        new(
            "app-data-folder-has-one-authority",
            Pattern(@"\bLocalAppData\s*,\s*""Deguffer"""),
            SourceView.CodeAndLiterals,
            Product,
            "Ask the one type that names Deguffer's data folder. If it does not exist yet, create it in this change, or ask.",
            "Path.Combine(environment.LocalAppData, \"Deguffer\", \"keep.json\")"),
        new(
            "link-refusal-sentence-has-one-authority",
            Pattern(@"is a link to somewhere else, and Deguffer"),
            SourceView.CodeAndLiterals,
            [Core],
            "Use CacheLevelWalk.Note, so every row words a link the same way.",
            "$\"Leaving '{path}' alone: it is a link to somewhere else, and Deguffer does not look through a link.\""),
        new(
            "plan-identity-stamped-once",
            Pattern(@"\bProviderId\s*=\s*Id\s*,"),
            SourceView.Code,
            [Core],
            "Build the plan through the base class, which stamps the row's identity and shared notes.",
            "new CleanupPlan { ProviderId = Id, ProviderName = Name }"),
        new(
            "overlap-has-one-authority",
            Pattern(@"\b(\w+)\s*\.\s*Holds\s*\(\s*(\w+)\s*\)\s*\|\|\s*\2\s*\.\s*Holds\s*\(\s*\1\s*\)|\bContains\s*\(\s*(\w+)\s*,\s*(\w+)\s*\)\s*\|\|\s*(?:[\w.]+\.)?Contains\s*\(\s*\4\s*,\s*\3\s*\)"),
            SourceView.Code,
            Product,
            "Ask the shared overlap test rather than writing both directions again. If it does not exist yet, create it in this change, or ask.",
            "a.Holds(b) || b.Holds(a)"),
        new(
            "step-kind-is-decided-by-the-step",
            Pattern($@"\b(?:is|as)\s+(?:not\s+)?(?:{StepTypes})\b|\bOfType\s*<\s*(?:{StepTypes})\s*>|\bcase\s+(?:{StepTypes})\b|(?<=(?:^|[,{{(])\s*)(?:not\s+)?(?:{StepTypes})(?:\s+\w+)?\s*(?:when\b[^\n]*?)?=>"),
            SourceView.Code,
            CoreAndApp,
            "Ask the step: give CleanupStep a member its kinds override, so a new kind cannot be missed (G1).",
            "if (step is RunCommandStep command)"),
        new(
            "tasks-are-observed",
            Pattern(@"(?:^|[;{}])\s*_\s*=\s*(?:[\w.?!]+Async|Task\s*\.\s*Run)\s*\("),
            SourceView.Code,
            Product,
            "Await it, or hand it to the type that records a fault, so a failure is never lost.",
            "_ = ViewModel.RefreshAsync();"),
        new(
            "selection-is-not-an-enum-ordinal",
            Pattern(@"(?:(?<![\w)\]]\s*)|\breturn\s+)\(\s*[A-Z]\w*\s*\)\s*[\w.]*\.\s*SelectedIndex\b|\bSelectedIndex\s*=\s*\(\s*int\s*\)"),
            SourceView.Code,
            [App],
            "Map the choice through an ordered list in Core, never the enum's ordinal.",
            "ViewModel.Order = (RowOrder)OrderBox.SelectedIndex;"),
        new(
            "one-pluraliser",
            Pattern(@"[!=]=\s*1\s*\?\s*\$?@?"""),
            SourceView.CodeAndLiterals,
            Product,
            "Use the one counted-noun formatter, so every count reads the same way. If it does not exist yet, create it in this change, or ask.",
            "count == 1 ? \"file\" : \"files\""),
        new(
            "parallel-options-are-bounded",
            Pattern(
                @"(?:\bnew\s+(?:[\w.]+\.)?ParallelOptions(?>\s*(?:\(\s*\)\s*)?)|\bParallelOptions\??\s+\w+\s*(?:\{[^}]*\}\s*)?=\s*new\s*\(\s*\)(?>\s*))(?:\{(?![^}]*\bMaxDegreeOfParallelism\s*=(?>\s*)(?!-))[^}]*\}|(?!\s*\{))"
                + @"|\bParallel\s*\.\s*(?:For|ForEach|Invoke)\s*\((?![^;]*(?:ParallelOptions|\bnew\s*\(\s*\)|\b\w*[Oo]ptions\b))"
                + @"|\bParallel\s*\.\s*(?:For|ForEach|Invoke|ForEachAsync)\s*\([^;]*?\bnew\s*\(\s*\)\s*\{(?![^}]*\bMaxDegreeOfParallelism\s*=(?>\s*)(?!-))"
                + @"|\.\s*AsParallel\s*\(\s*\)(?![^;]*\.\s*WithDegreeOfParallelism\s*\()"),
            SourceView.Code,
            Product,
            "Set MaxDegreeOfParallelism to a bound in the options' initialiser, from the walk tuning or DiskBoundWork where the work is the disk's (G4).",
            "var options = new ParallelOptions { CancellationToken = ct };"),
        new(
            "no-forced-collection",
            Pattern(@"\bGC\s*\.\s*Collect\s*\("),
            SourceView.Code,
            CoreAndApp,
            "Release what holds the memory, or hand back the buffers; a forced collection stalls every thread (G4).",
            "GC.Collect();"),
        new(
            "no-thread-sleep",
            Pattern(@"\bThread\s*\.\s*Sleep\s*\("),
            SourceView.Code,
            CoreAndApp,
            "Wait on what signals the condition, with the operation's token, or a TimeProvider delay a test can advance.",
            "Thread.Sleep(100);"),
        new(
            "whole-file-reads-are-own-stores",
            Pattern(@"\bFile\s*\.\s*ReadAll(?:Text|Bytes|Lines)(?:Async)?\s*\(|\b(?:XDocument|XElement|XmlDocument)\s*\.\s*Load\s*\("),
            SourceView.Code,
            CoreAndApp,
            "Read a file another program wrote through BoundedFile with its format's limit, and Deguffer's own store through StoreFile. If StoreFile has no reader yet, create it in this change, or ask.",
            "var text = File.ReadAllText(path);"),
        new(
            "no-powershell-start",
            Pattern(@"""[^""\n]*\b(?:powershell|pwsh)(?:\.exe\b|\s+-)[^""\n]*""", RegexOptions.IgnoreCase),
            SourceView.CodeAndLiterals,
            CoreAndApp,
            "Ask WMI through WbemConnection, a Win32 call or WindowsPrincipal in process. Only a vendor's own clear command may start PowerShell (§5.1).",
            "runner.RunAsync(\"powershell.exe\", arguments, ct)"),
    ];

    private static Regex Pattern(string pattern, RegexOptions extra = RegexOptions.None) =>
        new(pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant | extra);
}
