using Deguffer.Core.Viewing;

namespace Deguffer.App.Shell;

/// <summary>
/// How the reader wants things to move, asked by every animation as it starts and at each of its
/// frames. A seam so the shell's tests can answer for a reader with animation effects off without
/// changing the machine's setting. See <see cref="SystemMotion"/>.
/// </summary>
internal interface IMotionPolicy
{
    /// <summary>How an animation of <paramref name="token"/>'s kind is to play now.</summary>
    Motion For(MotionToken token);
}
