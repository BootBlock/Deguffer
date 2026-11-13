using System.Numerics;
using Deguffer.Core.Exploring.Rendering;
using Microsoft.UI.Composition;

namespace Deguffer.App.Controls;

/// <summary>
/// How the map's names fade as the picture shrinks round them, by step: one visual for each pair of
/// fade steps (<see cref="LabelFade.Step"/>) a drawing's names fall in, whose opacity the compositor
/// works out once for every name in it.
///
/// <para>By step rather than by name, because the camera changes on every frame of a pan or a zoom, and
/// an opacity per name would be an expression per name for the compositor to work out on every one of
/// them. The pairs of steps a drawing's names fall in are bounded by the steps there are, and do not
/// grow with the number of names.</para>
///
/// <para>A step's visual is as large as the visual over every name (see <see cref="NameLayer"/>), so a
/// name in it is carried with the picture as it would be there, and its opacity reads that visual's
/// size, which is the picture's magnification against the drawing's.</para>
///
/// <para>Separate from <see cref="NameLayer"/>, which carries every name with the picture, because
/// this decides only how opaque each is (G1).</para>
/// </summary>
internal sealed class NameFades
{
    /// <summary>The name the visual over every name is given in the steps' expressions.</summary>
    private const string Layer = "layer";

    private readonly Compositor _compositor;

    private readonly ContainerVisual _layer;

    private readonly Dictionary<(int Across, int Down), Step> _steps = [];

    /// <summary>The steps under the visual over every name, <paramref name="layer"/>.</summary>
    public NameFades(Compositor compositor, ContainerVisual layer)
    {
        _compositor = compositor;
        _layer = layer;
    }

    /// <summary>Fade <paramref name="name"/> as <paramref name="across"/> and <paramref name="down"/> say, by their steps.</summary>
    public void Join(Visual name, LabelFade across, LabelFade down)
    {
        var key = (across.Step, down.Step);

        if (!_steps.TryGetValue(key, out var step))
        {
            step = new Step(_compositor, key.Item1, key.Item2, _layer);
            _steps.Add(key, step);
        }

        if (name.Parent != step.Visual)
        {
            Leave(name);
            step.Visual.Children.InsertAtTop(name);
        }
    }

    /// <summary>Stop fading <paramref name="name"/>, which is put away.</summary>
    public static void Leave(Visual name) => (name.Parent as ContainerVisual)?.Children.Remove(name);

    /// <summary>
    /// Work out only the opacities of steps a name is in, once a drawing's names have joined theirs. A
    /// step left empty stops until a name joins it again, rather than being made again then.
    /// </summary>
    public void Settle()
    {
        foreach (var step in _steps.Values)
        {
            step.Settle();
        }
    }

    /// <summary>One pair of steps' visual, and the opacity it fades its names by.</summary>
    private sealed class Step
    {
        private readonly ExpressionAnimation _opacity;

        private readonly ContainerVisual _parent;

        private bool _running;

        public Step(Compositor compositor, int across, int down, ContainerVisual parent)
        {
            _parent = parent;

            Visual = compositor.CreateContainerVisual();
            Visual.RelativeSizeAdjustment = Vector2.One;

            var opacity = LabelFade.Opacity(
                LabelFade.AtStep(across),
                LabelFade.AtStep(down),
                new ExpressionScalar($"{Layer}.Size.X"),
                new ExpressionScalar($"{Layer}.Size.Y"));

            _opacity = compositor.CreateExpressionAnimation(opacity.Text);
            _opacity.SetReferenceParameter(Layer, parent);
        }

        public ContainerVisual Visual { get; }

        /// <summary>
        /// Run the opacity, with the step's visual under the layer, where a name is in the step, and
        /// stop it and take the visual off where none is.
        /// </summary>
        public void Settle()
        {
            var wanted = Visual.Children.Count > 0;

            if (wanted && !_running)
            {
                _parent.Children.InsertAtTop(Visual);
                Visual.StartAnimation(nameof(Visual.Opacity), _opacity);
            }
            else if (!wanted && _running)
            {
                Visual.StopAnimation(nameof(Visual.Opacity));
                _parent.Children.Remove(Visual);
            }

            _running = wanted;
        }
    }
}
