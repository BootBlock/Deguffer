namespace Deguffer.Core.Retrim;

/// <summary>What became of the retrim asked for on one volume.</summary>
/// <param name="Root">The volume's mount point, as the request named it.</param>
/// <param name="Answer">Windows' answer, or why Windows was not asked.</param>
public sealed record RetrimResult(string Root, RetrimAnswer Answer)
{
    /// <summary>The result in a sentence, in Windows' own terms.</summary>
    public string Sentence => RetrimReport.Result(Root, Answer);
}
