using System;

namespace Noname.AvatarTools
{
    [Serializable]
    public sealed class ComponentHoistingOptions
    {
        public bool SkinnedMeshRenderers = true;
        public bool MeshRenderers = true;
        public bool PhysBones = true;
        public bool PhysBoneColliders = true;
        public bool ContactSenders = true;
        public bool ContactReceivers = true;
        public bool VRCConstraints = true;
        public bool Raycasts = true;
        public bool HeadChop = true;
        public bool AudioSources = true;
        public bool Lights = true;
        public bool UnityColliders = true;

        public ComponentHoistingOptions Copy() => (ComponentHoistingOptions)MemberwiseClone();
    }
}
