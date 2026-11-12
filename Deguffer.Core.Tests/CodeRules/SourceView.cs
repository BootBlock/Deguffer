namespace Deguffer.Core.Tests.CodeRules;

/// <summary>Which parts of a C# file a rule reads. What a rule does not read is blanked, never removed.</summary>
internal enum SourceView
{
    /// <summary>Code only: comments and string and character literals are blanked.</summary>
    Code,

    /// <summary>Code and literals: comments are blanked, so a rule can find a sentence typed again.</summary>
    CodeAndLiterals,

    /// <summary>The file as written, for a rule about what comments say.</summary>
    Everything,
}
