using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf.animator;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Analysis
{
    // A property projection of the final Animator graphs, not a prediction of their
    // default state. Equal profiles have equal outputs in EVERY state of the same
    // execution contexts. Transitions, interruptions, layer weights and drivers can
    // then vary without assuming that independent state machines stay synchronized.
    internal sealed class BooleanAnimationEquivalence
    {
        private enum Kind { Missing, Number, Parameter, Child, Direct, Simple1D, SimpleDirectional2D,
            FreeformDirectional2D, FreeformCartesian2D, Profile }

        private sealed class Node : IEquatable<Node>
        {
            internal readonly Kind Kind;
            internal readonly string Text;
            internal readonly int[] Inputs;
            private readonly int hash;

            internal Node(Kind kind, string text, int[] inputs)
            {
                Kind = kind; Text = text; Inputs = inputs;
                unchecked
                {
                    hash = (int)kind * 397 ^ (text?.GetHashCode() ?? 0);
                    foreach (int input in inputs) hash = hash * 31 + input;
                }
            }

            public bool Equals(Node other) => other != null && Kind == other.Kind && Text == other.Text && Inputs.SequenceEqual(other.Inputs);
            public override bool Equals(object obj) => obj is Node other && Equals(other);
            public override int GetHashCode() => hash;
        }

        internal sealed class Profile
        {
            // Profiles are interned within one immutable index snapshot. Reference
            // equality must never compare proofs from different build iterations.
            internal readonly int Expression;
            internal Profile(int expression) { Expression = expression; }
        }

        private readonly AvatarAnimationIndex index;
        private readonly Dictionary<Node, int> nodes = new();
        private readonly Dictionary<int, Profile> profiles = new();
        private readonly Dictionary<(Object, string), Profile> cache = new();
        private readonly Dictionary<VirtualBlendTree, bool> normalizedDirect = new();
        private readonly (VirtualState State, bool Supported)[] states;
        private readonly bool supported;
        private const int Missing = 0, Unknown = -1, MaximumDepth = 128;

        internal BooleanAnimationEquivalence(AvatarAnimationIndex index)
        {
            this.index = index;
            Intern(Kind.Missing, null);
            // Synced-layer overrides and additive reference poses need a separate
            // evaluation model. The existing exact-writer proof remains available.
            supported = index.UnsafeReason == null;
            var additive = index.ControllerEntries.Where(e => e.Key is VRCAvatarDescriptor.AnimLayerType type &&
                type == VRCAvatarDescriptor.AnimLayerType.Additive).Select(e => e.Value).ToHashSet();
            // A synced layer can have no StateMachine of its own: it evaluates
            // the source layer's states with different motions/behaviours. Reject
            // semantic projections in that controller rather than overlooking
            // an execution context that AllReachableNodes cannot enumerate here.
            var synchronized = index.Controllers.Where(c => c.Layers.Any(l => l.SyncedLayerIndex != -1 ||
                l.SyncedLayerMotionOverrides.Count != 0)).ToHashSet();
            states = index.Controllers.SelectMany(c => c.Layers.SelectMany(l =>
                l.AllReachableNodes().OfType<VirtualState>().Select(s => (s, !additive.Contains(c) && !synchronized.Contains(c) &&
                    l.BlendingMode == AnimatorLayerBlendingMode.Override)))).ToArray();
        }

        private int Intern(Kind kind, string text, params int[] inputs)
        {
            var node = new Node(kind, text, inputs);
            if (!nodes.TryGetValue(node, out int id)) nodes.Add(node, id = nodes.Count);
            return id;
        }

        private int Number(float value) => Intern(Kind.Number, null, BitConverter.SingleToInt32Bits(value));
        private int Parameter(string name) => Intern(Kind.Parameter, name);

        private bool Normalized(VirtualBlendTree tree)
        {
            if (!normalizedDirect.TryGetValue(tree, out bool value))
                normalizedDirect.Add(tree, value = tree.NormalizedBlendValues);
            return value;
        }

        // This compares animation contributions only. The caller must separately
        // check initial values and effective mask coverage for the two targets.
        internal Profile For(Object target, string property)
        {
            if (cache.TryGetValue((target, property), out var cached)) return cached;
            var result = Analyze(target, property);
            cache.Add((target, property), result);
            return result;
        }

        private Profile Analyze(Object target, string property)
        {
            if (!supported) return null;
            var uses = index.ForObject(target).Where(u => u.Binding.propertyName == property).ToArray();
            if (uses.Length == 0 || uses.Any(u => u.Immutable || !u.Complete || u.Roots.Length != 1 ||
                u.Targets.Length != 1 || u.Objects().Count() != 1 || u.Curve == null || u.Binding.isPPtrCurve)) return null;
            var first = uses[0].Binding;
            if (uses.Any(u => u.Binding.type != first.type || u.Binding.isDiscreteCurve != first.isDiscreteCurve)) return null;
            // Two aliased tracks in one clip have an ordering/overwrite interaction.
            if (uses.GroupBy(u => u.Clip).Any(g => g.Count() != 1)) return null;
            var writers = new Dictionary<VirtualClip, int>();
            foreach (var use in uses)
            {
                // Reject nonconstant inputs before walking unrelated states.
                if (!MotionAnalysis.Constant(use.Curve, out float value)) return null;
                writers.Add(use.Clip, Number(value));
            }
            var memo = new Dictionary<(VirtualMotion, bool), int>();
            var visiting = new HashSet<VirtualMotion>();
            var seen = new HashSet<VirtualClip>();

            int Project(VirtualMotion motion, bool writeDefaults, int depth)
            {
                if (motion == null) return Missing;
                if (memo.TryGetValue((motion, writeDefaults), out int cached)) return cached;
                if (depth > MaximumDepth || !visiting.Add(motion)) return Unknown;
                int output = Unknown;
                if (motion is VirtualClip clip)
                {
                    if (!writers.TryGetValue(clip, out int value)) output = Missing;
                    else
                    {
                        seen.Add(clip);
                        // Constant leaves are independent of clip length, looping,
                        // time scale and transition clocks. Animated curves keep
                        // the stricter existing same-clip proof.
                        output = value;
                    }
                }
                else if (motion is VirtualBlendTree tree)
                {
                    var children = tree.Children;
                    var projected = children.Select(c => Project(c.Motion, writeDefaults, depth + 1)).ToArray();
                    if (projected.All(p => p == Missing)) output = Missing;
                    else if (projected.All(p => p != Unknown) && children.All(c => !c.Mirror &&
                        MotionAnalysis.Finite(c.TimeScale) && MotionAnalysis.Finite(c.CycleOffset)))
                    {
                        if (tree.BlendType == BlendTreeType.Direct)
                        {
                            // Direct trees with Write Defaults Off have history-
                            // dependent native evaluation. Normalized trees also
                            // observe non-writing siblings through their denominator.
                            if (writeDefaults && !Normalized(tree) && !first.isDiscreteCurve)
                            {
                                var terms = children.Select((c, i) => (Child: c, Output: projected[i]))
                                    .Where(c => c.Output != Missing).Select(c =>
                                        Intern(Kind.Child, null, Parameter(c.Child.DirectBlendParameter), c.Output)).ToArray();
                                // Keep writer order and multiplicity: this is not
                                // algebraic reassociation of floating-point sums.
                                output = Intern(Kind.Direct, null, terms);
                            }
                        }
                        else if (children.All(c => MotionAnalysis.Finite(c.Threshold) &&
                            MotionAnalysis.Finite(c.Position.x) && MotionAnalysis.Finite(c.Position.y)))
                        {
                            Kind? kind = tree.BlendType switch
                            {
                                BlendTreeType.Simple1D => Kind.Simple1D,
                                BlendTreeType.SimpleDirectional2D => Kind.SimpleDirectional2D,
                                BlendTreeType.FreeformDirectional2D => Kind.FreeformDirectional2D,
                                BlendTreeType.FreeformCartesian2D => Kind.FreeformCartesian2D,
                                _ => null
                            };
                            if (kind.HasValue && MotionAnalysis.Finite(tree.MinThreshold) && MotionAnalysis.Finite(tree.MaxThreshold))
                            {
                                var terms = new List<int> { Parameter(tree.BlendParameter) };
                                if (tree.BlendType != BlendTreeType.Simple1D) terms.Add(Parameter(tree.BlendParameterY));
                                else
                                {
                                    terms.Add(Number(tree.MinThreshold)); terms.Add(Number(tree.MaxThreshold));
                                    terms.Add(Number(tree.UseAutomaticThresholds ? 1 : 0));
                                }
                                for (int i = 0; i < children.Count; i++)
                                    terms.Add(tree.BlendType == BlendTreeType.Simple1D ?
                                        Intern(Kind.Child, null, Number(children[i].Threshold), projected[i]) :
                                        Intern(Kind.Child, null, Number(children[i].Position.x), Number(children[i].Position.y), projected[i]));
                                // Missing is deliberately distinct from a default-
                                // valued curve. It can hold a prior state/layer value.
                                output = Intern(kind.Value, null, terms.ToArray());
                            }
                        }
                    }
                }
                visiting.Remove(motion);
                memo[(motion, writeDefaults)] = output;
                return output;
            }

            var outputs = states.Select(s =>
            {
                int output = Project(s.State.Motion, s.State.WriteDefaultValues, 0);
                return output == Missing || s.Supported ? output : Unknown;
            }).ToArray();
            if (outputs.Any(p => p == Unknown) || seen.Count != writers.Count) return null;
            // State position is an execution identity, not just a matching graph
            // shape. Even identical graphs in different layers stay independent.
            int expression = Intern(Kind.Profile, first.type.FullName + ":" + property + ":" + first.isDiscreteCurve, outputs);
            if (!profiles.TryGetValue(expression, out var result)) profiles.Add(expression, result = new Profile(expression));
            return result;
        }
    }
}
