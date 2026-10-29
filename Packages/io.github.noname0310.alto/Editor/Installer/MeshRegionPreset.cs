using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools.Installer
{
    [Serializable]
    public sealed class MeshGeometryFingerprint
    {
        public string PositionsSha256;
        public string DistributionSignature;
    }

    // Contains identifiers and region names only, never mesh references or geometry samples.
    [CreateAssetMenu(menuName = "Noname/ALTO/Mesh Region Preset", fileName = "Mesh Region Preset")]
    public sealed class MeshRegionPreset : ScriptableObject
    {
        public string DisplayName;
        public List<string> BlendShapes = new();
        public List<MeshGeometryFingerprint> Fingerprints = new();
    }
}
