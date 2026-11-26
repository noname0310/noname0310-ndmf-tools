using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools.TestFixtures
{
    [ExecuteAlways]
    public sealed class AnimationEventReceiverFixture : MonoBehaviour
    {
        public sealed class Observation
        {
            public string FunctionName, StringParameter;
            public float Time, FloatParameter;
            public int IntParameter;
            public Object ObjectParameter;
            public bool FiredByAnimator;
        }

        public readonly List<Observation> Observations = new List<Observation>();

        public void ObserveAnimationEvent(AnimationEvent value)
        {
            Observations.Add(new Observation {
                FunctionName = value.functionName, Time = value.time,
                FloatParameter = value.floatParameter, IntParameter = value.intParameter,
                StringParameter = value.stringParameter, ObjectParameter = value.objectReferenceParameter,
                FiredByAnimator = value.isFiredByAnimator
            });
        }
    }
}
