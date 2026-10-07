using Deguffer.App.Shell;
using Deguffer.Core.Viewing;

namespace Deguffer.App.Tests;

/// <summary>
/// A reader's Animation effects setting that the test sets, and can change while an animation runs,
/// rather than the machine's.
/// </summary>
internal sealed class ScriptedMotion(bool animationsEnabled) : IMotionPolicy
{
    public bool AnimationsEnabled { get; set; } = animationsEnabled;

    public Motion For(MotionToken token) => token.For(AnimationsEnabled);
}
