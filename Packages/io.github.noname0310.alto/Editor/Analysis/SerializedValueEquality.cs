using System;
using System.Collections.Generic;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Noname.AvatarTools.Analysis
{
    internal static class SerializedValueEquality
    {
        internal static bool Matches(Object first, Object second, ISet<string> ignored)
        {
            if (first == second) return true;
            if (first == null || second == null || first.GetType() != second.GetType()) return false;
            using var a = new SerializedObject(first); using var b = new SerializedObject(second);
            var left = a.GetIterator(); var right = b.GetIterator();
            bool hasLeft = left.Next(true), hasRight = right.Next(true);
            while (hasLeft && hasRight)
            {
                if (left.propertyPath != right.propertyPath) return false;
                if (!ignored.Contains(left.propertyPath) && !SerializedProperty.DataEquals(left, right)) return false;
                hasLeft = left.Next(false); hasRight = right.Next(false);
            }
            return hasLeft == hasRight;
        }
    }
}
