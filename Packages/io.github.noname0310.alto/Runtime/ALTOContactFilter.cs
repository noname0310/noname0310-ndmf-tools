using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Dynamics.Contact.Components;

namespace Noname.AvatarTools
{
    public enum ContactFilterMode { RemoveListed, KeepListed }

    [Serializable]
    public sealed class ContactObjectSelection
    {
        [Tooltip("Select objects that hold Contact components, including Contacts generated under these objects during the build.")]
        public Transform Root;
        public bool IncludeChildren = true;
    }

    [AddComponentMenu("Noname/ALTO Contact Filter")]
    [DisallowMultipleComponent]
    public sealed class ALTOContactFilter : ALTOPlatformOptimizationComponent
    {
        public ContactFilterMode FilterMode = ContactFilterMode.KeepListed;
        public List<VRCContactSender> Senders = new();
        public List<VRCContactReceiver> Receivers = new();
        public List<ContactObjectSelection> ObjectRoots = new();
    }
}
